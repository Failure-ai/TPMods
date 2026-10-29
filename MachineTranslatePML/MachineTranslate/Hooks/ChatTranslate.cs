using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.NetModules;
using Terraria.GameContent.UI.Chat;
using Terraria.GameInput;
using Terraria.UI;
using Terraria.Net;
using Terraria.UI.Chat;
using tContentPatch;
using tContentPatch.Content.UI;
using tContentPatch.Content.UI.ModSet;
using MachineTranslate.Settings;
using MachineTranslate.Translators;

namespace MachineTranslate.Hooks
{

    // 一条聊天记录(原文+译文, 含面板排版缓存)
    public class ChatEntry
    {
        public string Text;
        public Color Color;
        public string Translated;
        public volatile bool Pending;

        // 翻译失败后的退避截止时间(TickCount毫秒)
        public int NextRetryTick;

        public string Lang;

        // 发送框请求: 译文进预览区, 不进历史
        public bool IsSend;

        // 翻译源: 剥掉"原文 xN"重复合并后缀(与缓存键一致), 兼作连续同文合并比对键; null=用Text
        public string TranslateSrc;

        // ---- 面板排版缓存(主线程读写, 宽度/译文/状态变化时失效) ----
        internal string[] CacheLines = null;
        internal int CacheOrigLines;
        internal float CacheHeight = -1f;
        internal float CacheW = -1f;
        internal string CacheTransRef = null;
        internal bool CachePending;
        internal bool CacheDone;
    }

    // 聊天翻译主体: 收消息(轮询聊天记录)→入史→排队翻译(仅彩云, 同步HTTP, 串行派发) + 历史面板。
    public static class ChatTranslate
    {
        private const int MaxEntries = 200;
        private const int FailCooldownMs = 10000;
        private const int EntryRetryMs = 20000;
        private const int AutoRetryMs = 30000;

        private const float TextScale = 0.8f;
        private const float BlockGap = 2f;
        private const float EntryGap = 6f;
        private const float WheelStep = 60f;
        private const float BarW = 8f;
        private const int MaxLinesPerBlock = 24;
        private const int DebounceMs = 1000;
        private const int SendIdle = 0;
        private const int SendBusy = 1;

        private static readonly List<ChatEntry> Entries = new List<ChatEntry>();
        private static readonly ConcurrentQueue<ChatEntry> WaitQueue = new ConcurrentQueue<ChatEntry>();

        private static bool _oldHeld;
        private static bool _panelVisible;
        private static bool _logOnce;
        private static int _lastAutoRetryTick = Environment.TickCount;

        private static volatile bool _inFlight;
        private static int _cooldownUntilTick;

        private static int _debounceStartTick = -1;
        private static int _sendState = SendIdle;
        private static string _sendHint;
        private static bool _sendHintIsError;
        private static int _sendHintUntilTick;
        private static bool _lastSendFocused;
        private static string _lastStatus;
        private static string _lastPreview;
        private static volatile string _sendEn;

        private static int _oldWheelValue;
        private static int _wheelDelta;

        private static List<ChatMessageContainer> _chatLog;
        private static ChatMessageContainer _lastSeenMsg;
        private static bool _chatLogPrimed;

        internal static bool PollerBound => _chatLog != null;

        // 每帧驱动(Main.Update prefix, 主线程)
        public static void Update()
        {
            PumpQueue();

            try
            {
                if (Main.dedServ) return;

                PollChatLog();

                AutoRetryTick();

                // 滚轮增量自采集: 游戏内置滚轮字段会被原版聊天/物品栏抢先消费置零
                Microsoft.Xna.Framework.Input.MouseState ms = Microsoft.Xna.Framework.Input.Mouse.GetState();
                _wheelDelta = ms.ScrollWheelValue - _oldWheelValue;
                _oldWheelValue = ms.ScrollWheelValue;

                if (!Settings.Settings.S.EnableChatTranslate && Panel.Visible)
                {
                    Panel.Hide();
                    _panelVisible = false;
                }

                if (_panelVisible && !Panel.Visible)
                {
                    Panel.Hide();
                    _panelVisible = false;
                }

                bool focused = Panel.SendFocused;
                if (focused && !_lastSendFocused)
                {
                    _debounceStartTick = -1;
                    Log("输入框已聚焦)");
                }
                else if (!focused && _lastSendFocused)
                {
                    Log("输入框失焦");
                }
                _lastSendFocused = focused;

                UpdatePanelToggle();

                DebounceTick();

            }
            catch (Exception ex)
            {
                LogOnce("按键轮询出错: " + ex.Message);
            }
        }

        private static void UpdatePanelToggle()
        {
            bool held = false;
            if (Settings.Settings.S.EnableChatTranslate && !Main.gameMenu && !Main.drawingPlayerChat
                && !Panel.SendFocused)
            {
                Microsoft.Xna.Framework.Input.Keys key = Settings.Settings.GetTranslateKey();
                held = PlayerInput.GetPressedKeys().Contains(key);
            }

            if (held != _oldHeld)
            {
                _oldHeld = held;
                if (held && Settings.Settings.S.EnableChatTranslate)
                {

                    if (_panelVisible)
                    {
                        _panelVisible = false;
                        Panel.Hide();
                    }
                    else if (Panel.Show())
                    {
                        _panelVisible = true;
                        Panel.FollowBottom();
                    }
                    TranslateMissing();
                }
            }
        }

        private static void DebounceTick()
        {
            if (!Panel.SendFocused || _sendState != SendIdle || _debounceStartTick < 0) return;

            string trim = Panel.SendTrimmed();
            if (trim == null || trim.Length == 0) return;
            if ((uint)(Environment.TickCount - _debounceStartTick) < (uint)DebounceMs) return;

            _debounceStartTick = -1;
            string cached;
            if (TranslatorCore.TryGetCacheLang(trim, "en", out cached))
            {

                _sendEn = cached;
            }
            else
            {
                _sendState = SendBusy;
                _sendEn = null;

                ChatEntry send = new ChatEntry();
                send.Text = trim;
                send.Lang = "en";
                send.IsSend = true;
                send.Pending = true;
                WaitQueue.Enqueue(send);
                Log("发送框开始翻译");
            }
        }

        // 收消息唯一触发源: 每帧轮询原版 RemadeChatMonitor._messages(所有模组改写后的最终入库
        // 数据, index 0=最新, 实例终身不换)。水位=容器引用比对; 找不到水位(被清除重复聊天删头等)
        // 只把[0]当唯一新消息
        private static void PollChatLog()
        {
            if (_chatLog == null)
            {
                BindChatLog();
                if (_chatLog == null) return;
            }

            int count = _chatLog.Count;
            if (count == 0)
            {
                _lastSeenMsg = null;
                _chatLogPrimed = false;
                return;
            }

            ChatMessageContainer top = _chatLog[0];
            if (top == null || top == _lastSeenMsg) return;

            if (!_chatLogPrimed)
            {

                _chatLogPrimed = true;
                _lastSeenMsg = top;
                if (count == 1) HandleLogEntry(top);
                return;
            }

            int newCount = 1;
            int cap = Math.Min(count, 32);
            for (int i = 1; i < cap; i++)
            {
                if (_chatLog[i] == _lastSeenMsg) { newCount = i; break; }
            }

            for (int i = newCount - 1; i >= 0; i--)
            {
                HandleLogEntry(_chatLog[i]);
            }
            _lastSeenMsg = top;
        }

        // 一次性反射绑定聊天记录列表
        private static void BindChatLog()
        {
            try
            {
                if (Main.chatMonitor == null) return;
                FieldInfo f = typeof(RemadeChatMonitor).GetField("_messages",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return;
                _chatLog = f.GetValue(Main.chatMonitor) as List<ChatMessageContainer>;
            }
            catch (Exception)
            {
            }
        }

        private static void HandleLogEntry(ChatMessageContainer c)
        {
            try
            {
                if (c == null || c.OriginalText == null) return;

                OnNewMessage(c.OriginalText, Color.White);
            }
            catch (Exception)
            {
            }
        }

        // 剥掉 OptimizeAndTool 清除重复聊天合并出的"原文 xN"计数后缀
        private static string StripRepeatCounter(string text)
        {
            if (text == null) return null;
            int i = text.LastIndexOf(" x", StringComparison.Ordinal);
            if (i <= 0 || i + 2 >= text.Length) return text;
            for (int j = i + 2; j < text.Length; j++)
            {
                if (text[j] < '0' || text[j] > '9') return text;
            }
            return text.Substring(0, i);
        }

        // 处理一条最终入库的聊天文本: 连续同文合并进最新条目; 新条目记录原文并排队翻译。
        public static void OnNewMessage(string text, Color color)
        {
            try
            {
                if (text == null) return;

                string src = StripRepeatCounter(text);

                ChatEntry last = Entries.Count > 0 ? Entries[Entries.Count - 1] : null;
                if (last != null && !last.IsSend
                    && string.Equals(last.TranslateSrc, src, StringComparison.Ordinal)
                    && last.Lang == TranslatorCore.TargetLang)
                {
                    last.Text = text;
                    last.CacheLines = null;
                    last.CacheHeight = -1f;
                    return;
                }

                ChatEntry entry = new ChatEntry();
                entry.Text = text;
                entry.TranslateSrc = src;
                entry.Color = color;
                entry.Lang = TranslatorCore.TargetLang;
                Entries.Add(entry);
                Trim();

                if (!Settings.Settings.S.EnableChatTranslate || Main.gameMenu) return;
                if (!src.IsTranslatable()) return;

                string cached;
                if (TranslatorCore.TryGetCache(src, out cached))
                {
                    entry.Translated = cached;
                }
                else
                {
                    StartTranslate(entry);
                }
            }
            catch (Exception)
            {
            }
        }

        // 退出世界时清空历史并复位发送区
        public static void ClearEntries()
        {
            Entries.Clear();

            ChatEntry stale;
            while (WaitQueue.TryDequeue(out stale))
            {
            }

            _debounceStartTick = -1;
            _sendState = SendIdle;
            _sendHint = null;
            _sendEn = null;
            _panelVisible = false;
            Panel.ResetWorld();
        }

        private static void StartTranslate(ChatEntry entry)
        {
            if (entry.Pending) return;
            if (entry.Translated != null) return;

            entry.Pending = true;
            WaitQueue.Enqueue(entry);
        }

        private static void AutoRetryTick()
        {
            int now = Environment.TickCount;
            if ((uint)(now - _lastAutoRetryTick) < (uint)AutoRetryMs) return;
            _lastAutoRetryTick = now;
            TranslateMissing();
        }

        // 把缺失译文的记录补入队(失败退避到期后重试, 面板开/关都翻)
        private static void TranslateMissing()
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                ChatEntry e = Entries[i];
                if (e.Translated == null && !e.Pending && e.Text.IsTranslatable()
                    && Environment.TickCount >= e.NextRetryTick)
                {
                    StartTranslate(e);
                }
            }
        }

        // 串行派发队列(每帧最多派发一个, 同一时刻只有一个请求在途; 失败有全局冷却)
        private static void PumpQueue()
        {
            if (_inFlight) return;
            if (Environment.TickCount < _cooldownUntilTick) return;

            ChatEntry entry;
            while (WaitQueue.TryDequeue(out entry))
            {
                if (entry.Translated != null)
                {

                    entry.Pending = false;
                    continue;
                }

                entry.Pending = true;

                string lang = string.IsNullOrEmpty(entry.Lang) ? TranslatorCore.TargetLang : entry.Lang;

                string src = string.IsNullOrEmpty(entry.TranslateSrc) ? entry.Text : entry.TranslateSrc;
                bool dispatched = TranslatorCore.TranslateTextLang(src, lang, delegate (string result)
                {
                    OnTranslateDone(entry, result);
                });

                if (!dispatched)
                {

                    entry.Pending = false;
                    if (entry.IsSend) WaitQueue.Enqueue(entry);
                    return;
                }

                _inFlight = true;
                return;
            }
        }

        // 单笔翻译完成(后台线程回调): 只写字段, 不触碰UI与聊天区
        private static void OnTranslateDone(ChatEntry entry, string result)
        {
            _inFlight = false;

            if (entry.IsSend)
            {

                _sendState = SendIdle;

                if (result == null)
                {
                    _debounceStartTick = Environment.TickCount;
                    _sendHint = "翻译失败, 可直接点发送(发原文)";
                    _sendHintIsError = true;
                    _sendHintUntilTick = Environment.TickCount + 4000;
                    return;
                }

                string cur = Panel.SendTrimmed();
                if (!string.IsNullOrEmpty(cur))
                {
                    _sendEn = result;
                    Log("发送框译文就绪(输入框下方预览区可查, 点右侧按钮发送)");
                }
                return;
            }

            if (result == null)
            {

                entry.Pending = false;
                entry.NextRetryTick = Environment.TickCount + EntryRetryMs;
                _cooldownUntilTick = Environment.TickCount + FailCooldownMs;
                return;
            }

            entry.Pending = false;
            entry.Translated = result;
        }

        private static void Trim()
        {
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        }

        // 按当前宽度换行并缓存
        private static void EnsureWrapped(ChatEntry e, float wrapW)
        {
            bool done = e.Translated != null;
            if (e.CacheLines != null && e.CacheW == wrapW && e.CacheTransRef == e.Translated
                && e.CachePending == e.Pending && e.CacheDone == done) return;

            var font = FontAssets.MouseText.Value;
            if (font == null) return;

            string[] orig = WrapText(e.Text, wrapW);
            string[] trans;
            if (e.Translated != null)
            {
                trans = WrapText(e.Translated, wrapW);
            }
            else if (e.Pending)
            {
                trans = new string[] { "翻译中..." };
            }
            else
            {
                trans = new string[] { "(翻译失败, 稍后自动重试)" };
            }

            string[] all = new string[orig.Length + trans.Length];
            orig.CopyTo(all, 0);
            trans.CopyTo(all, orig.Length);

            float h = 0f;
            for (int i = 0; i < all.Length; i++)
            {
                h += ChatManager.GetStringSize(font, all[i], Vector2.One, -1f).Y * TextScale;
                if (i == orig.Length - 1) h += BlockGap;
            }

            e.CacheLines = all;
            e.CacheOrigLines = orig.Length;
            e.CacheHeight = Math.Max(h, 8f);
            e.CacheW = wrapW;
            e.CacheTransRef = e.Translated;
            e.CachePending = e.Pending;
            e.CacheDone = done;
        }

        private static string[] WrapText(string text, float maxW)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines.ToArray();

            var font = FontAssets.MouseText.Value;
            if (font == null) return new string[] { text };

            string[] paras = text.Split('\n');
            for (int p = 0; p < paras.Length; p++)
            {
                string para = paras[p];
                if (para.Length == 0)
                {
                    lines.Add("");
                    continue;
                }

                int start = 0;
                while (start < para.Length)
                {
                    if (lines.Count >= MaxLinesPerBlock)
                    {
                        lines.Add("…");
                        return lines.ToArray();
                    }

                    int lo = 1, hi = para.Length - start, best = 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        float w = ChatManager.GetStringSize(font, para.Substring(start, mid), Vector2.One, -1f).X;
                        if (w <= maxW)
                        {
                            best = mid;
                            lo = mid + 1;
                        }
                        else
                        {
                            hi = mid - 1;
                        }
                    }

                    lines.Add(para.Substring(start, best));
                    start += best;
                }
            }

            return lines.ToArray();
        }

        private static void DrawEntry(ChatEntry e, float textX, float cy, float viewTop, float viewBottom)
        {
            var font = FontAssets.MouseText.Value;
            if (font == null) return;

            string[] lines = e.CacheLines;
            int origCount = Math.Min(e.CacheOrigLines, lines.Length);
            float y = cy;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                float lineH = ChatManager.GetStringSize(font, line, Vector2.One, -1f).Y * TextScale;

                if (line.Length > 0 && y >= viewTop - 0.5f && y + lineH <= viewBottom + 0.5f)
                {
                    Color col;
                    if (i < origCount) col = Color.LightGray;
                    else if (e.Translated != null) col = new Color(255, 240, 150);
                    else if (e.Pending) col = Color.Gray;
                    else col = new Color(255, 130, 130);

                    ChatManager.DrawColorCodedStringWithShadow(Main.spriteBatch, font, line,
                        new Vector2(textX, y), col, 0f, Vector2.Zero, new Vector2(TextScale));
                }

                y += lineH;
                if (i == origCount - 1) y += BlockGap;
            }
        }

        private static bool MouseIn(float x, float y, float w, float h)
        {
            return Main.mouseX >= x && Main.mouseX < x + w && Main.mouseY >= y && Main.mouseY < y + h;
        }

        // 发送: 优先发预览译文, 无译文发原文; 多人走 NetTextModule, 单人 Main.NewText
        internal static void SendCurrent()
        {
            string src = Panel.SendTrimmed();
            if (string.IsNullOrEmpty(src)) return;

            string text = _sendEn != null ? _sendEn : src;

            try
            {
                if (Main.netMode == 0)
                {
                    Main.NewText(text);
                }
                else
                {
                    NetPacket packet = NetTextModule.SerializeClientMessage(
                        ChatManager.Commands.CreateOutgoingMessage(text));
                    NetManager.Instance.SendToServer(packet);
                }
            }
            catch (Exception ex)
            {
                LogOnce("发送聊天消息失败: " + ex.Message);
                _sendHint = "发送失败: " + ex.Message;
                _sendHintIsError = true;
                _sendHintUntilTick = Environment.TickCount + 4000;
                return;
            }

            _sendEn = null;
            _sendState = SendIdle;
            _debounceStartTick = -1;
            if (Panel.Win != null && Panel.Win.SendBox != null) Panel.Win.SendBox.SetText("");
            _sendHint = "已发送: " + (text.Length > 40 ? text.Substring(0, 40) + "…" : text);
            _sendHintIsError = false;
            _sendHintUntilTick = Environment.TickCount + 3000;
        }

        private static void LogOnce(string msg)
        {
            if (_logOnce) return;
            _logOnce = true;

            Log(msg);
        }

        private static void Log(string msg)
        {
            string line = "[机器翻译] " + msg;
            //调试用//try { Console.WriteLine(line); } catch (Exception) { }
        }
        internal static void HostTick(GameTime gameTime)
        {
            Panel.HostTick(gameTime);
        }
        internal static void InsertPanelLayer(List<GameInterfaceLayer> layers)
        {
            Panel.InsertPanelLayer(layers);
        }
        private static class Panel
        {
            internal static UIState State;
            internal static TranslateWindow Win;

            internal static readonly UserInterface UI = new UserInterface();

            static Panel()
            {
                if (Main.dedServ) return;

                State = new UIState();
                Win = new TranslateWindow();
            }

            internal static void InsertPanelLayer(List<GameInterfaceLayer> layers)
            {
                LegacyGameInterfaceLayer panelLayer = new LegacyGameInterfaceLayer(
                    "MachineTranslate: Panel",
                    delegate
                    {
                        if (Win != null && Win.IsOpen && _panelVisible)
                        {
                            UI.Draw(Main.spriteBatch, Main.gameTimeCache);
                        }
                        return true;
                    },
                    InterfaceScaleType.UI);

                int idx = layers.FindIndex(i => i.Name == "Vanilla: Inventory");
                if (idx != -1) layers.Insert(idx, panelLayer);
                else layers.Add(panelLayer);
            }

            internal static bool Visible
            {
                get { return Win != null && Win.IsOpen; }
            }

            internal static bool SendFocused
            {
                get { return Win != null && Win.IsOpen && Win.SendBox != null && Win.SendBox.Focus; }
            }

            internal static string SendTrimmed()
            {
                if (Win == null || Win.SendBox == null) return null;
                string t = Win.SendBox.GetText();
                return t == null ? null : t.Trim();
            }

            internal static bool Show()
            {
                if (Win == null || Main.dedServ) return false;
                if (Main.gameMenu) return false;
                if (Main.inFancyUI) return false;
                if (Win.IsOpen) return true;

                Win.Open(State);
                Log("v" + ThisMod.ModVersion + " 面板开启(游戏内浮窗, 背包/血量/聊天照常显示)");
                return true;
            }

            internal static void CloseFromUI()
            {
                _panelVisible = false;
                Hide();
            }

            internal static void Hide()
            {
                if (Win == null) return;

                if (Win.IsOpen) Win.Close();

                UI.SetState(null);
            }

            internal static void HostTick(GameTime gameTime)
            {
                if (Win == null || State == null) return;

                if (Win.IsOpen && _panelVisible)
                {
                    UI.SetState(State);
                    UI.Recalculate();
                    UI.Update(gameTime);
                }
                else if (UI.CurrentState != null)
                {
                    UI.SetState(null);
                }
            }

            internal static void FollowBottom()
            {
                if (Win != null && Win.History != null) Win.History.Follow();
            }

            internal static void ResetWorld()
            {
                Hide();
                if (Win != null && Win.SendBox != null) Win.SendBox.SetText("");
                if (Win != null && Win.Preview != null) Win.Preview.SetText("");
                _debounceStartTick = -1;
            }

            internal static void RefreshPreview()
            {
                if (Win == null || !Win.IsOpen || Win.Preview == null) return;

                string txt;
                Color col;
                if (_sendState == SendBusy)
                {
                    txt = "译文: 翻译中…";
                    col = new Color(255, 240, 150);
                }
                else if (_sendEn != null)
                {
                    txt = "译文: " + _sendEn;
                    col = new Color(160, 255, 160);
                }
                else if (_sendHint != null)
                {
                    if (Environment.TickCount >= _sendHintUntilTick)
                    {
                        _sendHint = null;
                        txt = "";
                        col = new Color(150, 155, 185);
                    }
                    else
                    {
                        txt = _sendHint;
                        col = _sendHintIsError ? new Color(255, 130, 130) : new Color(160, 255, 160);
                    }
                }
                else
                {
                    txt = "";
                    col = new Color(150, 155, 185);
                }

                var font = FontAssets.MouseText.Value;
                if (font != null && txt.Length > 1 && Win.Child.GetDimensions().Width > 40f)
                {
                    float maxW = Win.Child.GetDimensions().Width - 72f;
                    while (txt.Length > 1 && ChatManager.GetStringSize(font, txt, Vector2.One, -1f).X > maxW)
                    {
                        txt = txt.Substring(0, txt.Length - 1);
                    }
                }

                if (txt == _lastPreview) return;
                _lastPreview = txt;
                Win.Preview.SetText(txt);
                Win.Preview.TextColor = col;
            }

            internal static void RefreshStatus()
            {
                if (Win == null || !Win.IsOpen || Win.Status == null) return;

                RefreshPreview();

                string txt;
                Color col = new Color(150, 155, 185);
                if (_sendState == SendBusy)
                {
                    txt = "翻译中… 输入框下方右侧按钮发送";
                    col = new Color(255, 240, 150);
                }
                else
                {
                    txt = "v" + ThisMod.ModVersion + " · " + Settings.Settings.S.TranslateKey + " 开/关此面板 · 翻译历史 "
                        + Entries.Count + " 条 · 点发送框输入中文停1秒译英文, 下方右侧按钮发送";
                }

                var font = FontAssets.MouseText.Value;
                if (font != null && txt.Length > 1 && Win.Child.GetDimensions().Width > 40f)
                {
                    float maxW = Win.Child.GetDimensions().Width / 0.75f - 8f;
                    while (txt.Length > 1 && ChatManager.GetStringSize(font, txt, Vector2.One, -1f).X > maxW)
                    {
                        txt = txt.Substring(0, txt.Length - 1);
                    }
                }

                if (txt == _lastStatus) return;
                _lastStatus = txt;
                Win.Status.SetText(txt);
                Win.Status.TextColor = col;
            }
        }

        private class TranslateWindow : UIWindow
        {
            public readonly HistoryView History;
            public readonly UIItemTextBox SendBox;
            public readonly SendButton SendBtn;
            public readonly Terraria.GameContent.UI.Elements.UIText Preview;
            public readonly SmallText Status;

            public TranslateWindow() : base("聊天翻译机 v" + ThisMod.ModVersion, 560, 400)
            {
                MinWidth.Pixels = 320;
                MinHeight.Pixels = 240;

                Status = new SmallText();
                Status.Width.Set(0, 1);
                Status.Height.Pixels = 16;
                Status.Top.Set(2, 0);
                Status.TextColor = new Color(150, 155, 185);
                Child.Append(Status);

                History = new HistoryView();
                History.Width.Set(0, 1);
                History.Height.Set(-116, 1);
                History.Top.Set(20, 0);
                Child.Append(History);

                SendBox = new UIItemTextBox("点击输入中文, 停1秒自动译成英文", 500, null, "发送文本:");
                SendBox.Width.Set(0, 1);
                SendBox.Height.Pixels = 40;
                SendBox.Top.Set(-96, 1);
                SendBox.TextBoxWidth = new StyleDimension(-4, 1);

                SendBox.OnTextChanged += delegate (string s)
                {
                    _debounceStartTick = Environment.TickCount;
                    _sendEn = null;
                };

                Child.Append(SendBox);

                FixSendBoxTextAlign();

                SendBtn = new SendButton();
                SendBtn.Width.Set(56, 0);
                SendBtn.Height.Set(30, 0);
                SendBtn.Top.Set(-52, 1);
                SendBtn.HAlign = 1;
                Child.Append(SendBtn);

                Preview = new Terraria.GameContent.UI.Elements.UIText("");
                Preview.Width.Set(-64, 1);
                Preview.Height.Pixels = 30;
                Preview.Top.Set(-52, 1);
                Preview.TextColor = new Color(150, 155, 185);
                Child.Append(Preview);
            }

            private void FixSendBoxTextAlign()
            {
                try
                {
                    FieldInfo fBox = typeof(UIItemTextBox).GetField("textBox", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo fText = typeof(tContentPatch.Content.UI.UITextBox).GetField("ui_text", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fBox == null || fText == null)
                    {
                        Log("诊断: 输入框内部字段反射定位失败(textBox=" + (fBox != null) + ", ui_text=" + (fText != null) + ")");
                        return;
                    }

                    var box = fBox.GetValue(SendBox) as tContentPatch.Content.UI.UITextBox;
                    var uitext = fText.GetValue(box) as Terraria.GameContent.UI.Elements.UIText;
                    if (uitext == null)
                    {
                        Log("诊断: 输入框内部 UIText 反射获取失败");
                        return;
                    }

                    uitext.TextOriginX = 0f;
                    uitext.Width.Set(0, 1);
                    uitext.Recalculate();
                    Log("输入框文本对齐已修正(TextOriginX=0, 长文本从左缘显示只裁右端)");
                }
                catch (Exception ex)
                {
                    Log("诊断: 输入框文本对齐修正异常: " + ex.Message);
                }
            }

            public override void Update(GameTime gameTime)
            {
                base.Update(gameTime);

                try
                {
                    if (PlayerInput.Triggers.JustPressed.Inventory)
                    {
                        Panel.CloseFromUI();
                        return;
                    }

                    // 悬停面板时防误挥武器
                    if (ContainsPoint(Main.MouseScreen))
                    {
                        Main.LocalPlayer.mouseInterface = true;
                    }

                    Panel.RefreshStatus();
                }
                catch (Exception)
                {
                }
            }
        }

        private class SmallText : UIElement
        {
            private string _text = "";
            public Color TextColor = new Color(150, 155, 185);

            public void SetText(string t)
            {
                _text = t ?? "";
            }

            protected override void DrawSelf(SpriteBatch spriteBatch)
            {
                if (_text.Length == 0) return;

                var font = FontAssets.MouseText.Value;
                if (font == null) return;

                ChatManager.DrawColorCodedStringWithShadow(spriteBatch, font, _text,
                    new Vector2(GetDimensions().X, GetDimensions().Y), TextColor, 0f,
                    Vector2.Zero, new Vector2(0.75f));
            }
        }

        private class SendButton : UIElement
        {
            private bool _oldMouseLeft;

            public override void Update(GameTime gameTime)
            {
                base.Update(gameTime);

                if (Main.mouseLeft && !_oldMouseLeft && IsMouseHovering)
                {
                    ChatTranslate.SendCurrent();
                }

                _oldMouseLeft = Main.mouseLeft;
            }

            protected override void DrawSelf(SpriteBatch spriteBatch)
            {
                CalculatedStyle dim = GetDimensions();
                var tex = TextureAssets.MagicPixel.Value;
                if (tex == null) return;

                Rectangle rect = new Rectangle((int)dim.X, (int)dim.Y, (int)dim.Width, (int)dim.Height);
                Color bg;
                if (Main.mouseLeft && IsMouseHovering) bg = new Color(90, 160, 90, 230);
                else if (IsMouseHovering) bg = new Color(110, 190, 110, 220);
                else bg = new Color(80, 130, 80, 200);
                spriteBatch.Draw(tex, rect, bg);

                var font = FontAssets.MouseText.Value;
                if (font == null) return;

                string label = "发送";
                Vector2 size = ChatManager.GetStringSize(font, label, Vector2.One, -1f) * 0.9f;
                ChatManager.DrawColorCodedStringWithShadow(spriteBatch, font, label,
                    new Vector2(dim.X + (dim.Width - size.X) / 2f, dim.Y + (dim.Height - size.Y) / 2f),
                    Color.White, 0f, Vector2.Zero, new Vector2(0.9f));
            }
        }

        private class HistoryView : UIElement
        {
            private float _scroll;
            private bool _follow = true;
            private bool _dragging;
            private float _dragGrabY;
            private bool _oldMouseLeft;

            internal void Follow()
            {
                _follow = true;
            }

            public override void Update(GameTime gameTime)
            {
                base.Update(gameTime);

                CalculatedStyle dim = GetDimensions();
                float viewX = dim.X, viewY = dim.Y, viewH = dim.Height;
                float innerW = dim.Width - BarW;
                if (innerW < 60f || viewH < 40f)
                {
                    _oldMouseLeft = Main.mouseLeft;
                    _wheelDelta = 0;
                    return;
                }

                float wrapW = innerW / TextScale;
                for (int i = 0; i < Entries.Count; i++) EnsureWrapped(Entries[i], wrapW);

                float totalH = TotalHeight();
                float maxScroll = Math.Max(0f, totalH - viewH);

                _scroll = MathHelper.Clamp(_scroll, 0f, maxScroll);
                if (_follow) _scroll = maxScroll;

                float trackX = viewX + innerW + 1f;
                float trackY = viewY + 2f;
                float trackH = viewH - 4f;
                float thumbH = Math.Max(26f, trackH * viewH / Math.Max(1f, totalH));
                float thumbY = trackY + (trackH - thumbH) * (maxScroll > 0f ? (_scroll / maxScroll) : 0f);

                bool overArea = MouseIn(viewX, viewY, dim.Width, viewH);

                if (_dragging)
                {
                    if (!Main.mouseLeft)
                    {
                        _dragging = false;
                    }
                    else if (maxScroll > 0f && trackH - thumbH > 1f)
                    {
                        float t = (Main.mouseY - _dragGrabY - trackY) / (trackH - thumbH);
                        _scroll = MathHelper.Clamp(t, 0f, 1f) * maxScroll;
                        _follow = _scroll >= maxScroll - 20f;
                    }
                }
                else
                {

                    if (overArea && _wheelDelta != 0 && maxScroll > 0f)
                    {
                        _scroll -= Math.Sign(_wheelDelta) * WheelStep;
                        _follow = _scroll >= maxScroll - 20f;
                    }
                    _wheelDelta = 0;

                    if (Main.mouseLeft && !_oldMouseLeft && maxScroll > 0f
                        && MouseIn(trackX - 2f, thumbY, BarW + 4f, thumbH))
                    {
                        _dragging = true;
                        _dragGrabY = Main.mouseY - thumbY;
                    }
                }

                _scroll = MathHelper.Clamp(_scroll, 0f, maxScroll);
                _oldMouseLeft = Main.mouseLeft;
            }

            protected override void DrawSelf(SpriteBatch spriteBatch)
            {
                CalculatedStyle dim = GetDimensions();
                float viewX = dim.X, viewY = dim.Y, viewH = dim.Height;
                float innerW = dim.Width - BarW;
                if (innerW < 60f || viewH < 40f) return;

                float wrapW = innerW / TextScale;
                for (int i = 0; i < Entries.Count; i++) EnsureWrapped(Entries[i], wrapW);

                float totalH = TotalHeight();
                float maxScroll = Math.Max(0f, totalH - viewH);
                _scroll = MathHelper.Clamp(_scroll, 0f, maxScroll);

                float viewBottom = viewY + viewH;
                float cy = viewY - _scroll;
                for (int i = 0; i < Entries.Count; i++)
                {
                    ChatEntry e = Entries[i];
                    if (cy > viewBottom) break;
                    if (e.CacheLines != null && cy + e.CacheHeight > viewY)
                    {
                        DrawEntry(e, viewX, cy, viewY, viewBottom);
                    }
                    cy += e.CacheHeight + EntryGap;
                }

                if (maxScroll > 0.5f)
                {
                    DrawScrollBar(spriteBatch, viewX, viewY, innerW, viewH, totalH, maxScroll);
                }
            }

            private float TotalHeight()
            {
                float totalH = 0f;
                for (int i = 0; i < Entries.Count; i++)
                {
                    totalH += Entries[i].CacheHeight;
                    if (i < Entries.Count - 1) totalH += EntryGap;
                }
                return totalH;
            }

            private void DrawScrollBar(SpriteBatch spriteBatch, float viewX, float viewY, float innerW, float viewH, float totalH, float maxScroll)
            {
                var tex = TextureAssets.MagicPixel.Value;
                if (tex == null) return;

                float trackX = viewX + innerW + 1f;
                float trackY = viewY + 2f;
                float trackH = viewH - 4f;
                float thumbH = Math.Max(26f, trackH * viewH / Math.Max(1f, totalH));
                float thumbY = trackY + (trackH - thumbH) * (MathHelper.Clamp(_scroll, 0f, maxScroll) / maxScroll);

                Rectangle trackRect = new Rectangle((int)trackX, (int)trackY, (int)BarW - 1, (int)trackH);
                spriteBatch.Draw(tex, trackRect, new Color(0, 0, 0, 90));

                bool hot = _dragging || MouseIn(trackX - 2f, thumbY, BarW + 4f, thumbH);
                Color thumbCol = hot ? new Color(215, 220, 240, 220) : new Color(165, 170, 195, 170);
                Rectangle thumbRect = new Rectangle((int)trackX, (int)thumbY, (int)BarW - 1, (int)thumbH);
                spriteBatch.Draw(tex, thumbRect, thumbCol);
            }
        }
    }

    public class MainHooks : PatchMain
    {
        public override void UpdatePrefix(Microsoft.Xna.Framework.GameTime gameTime)
        {
            ChatTranslate.Update();
        }

        public override void OnEnterWorld()
        {
            ChatTranslate.ClearEntries();
        }

        public override void UpdatePostfix(Microsoft.Xna.Framework.GameTime gameTime)
        {
            try
            {
                Terraria.GameInput.PlayerInput.SetZoom_UI();
                ChatTranslate.HostTick(gameTime);
            }
            catch (Exception) { }
            finally
            {
                try { Terraria.GameInput.PlayerInput.SetZoom_Unscaled(); } catch (Exception) { }
            }
        }

        public override void SetupDrawInterfaceLayersPostfix(List<GameInterfaceLayer> gameInterfaceLayers)
        {
            ChatTranslate.InsertPanelLayer(gameInterfaceLayers);
        }
    }

    public class ChatMonitorHooks : PatchRemadeChatMonitor
    {
        public override void AddNewMessagePrefix(ref string text, Color color, int widthLimitInPixels = -1)
        {
            if (ChatTranslate.PollerBound) return;
            ChatTranslate.OnNewMessage(text, color);
        }
    }
}
