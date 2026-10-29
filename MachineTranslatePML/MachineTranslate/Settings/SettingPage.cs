using System;
using tContentPatch;
using tContentPatch.Content.UI.ModSet;
using Terraria;
using Terraria.UI;

namespace MachineTranslate.Settings
{

    // 中文设置页(模组设置-翻译): 开关/快捷键/API/目标语言
    internal class SettingPage : ModSetting
    {
        public override string Name => "设置";
        public override string Title => "翻译: 设置";

        public override string FilePath => null;
        public override Type DataType => typeof(SettingsData);

        private Action _updateUi = null;

        public override void Load(object v)
        {

            Settings.LoadFromDisk();
            Settings.Page = this;
        }

        public override object GetSaveData()
        {
            return Settings.S;
        }

        public override void SetDefault()
        {
            Settings.S = new SettingsData();
            SaveToDiskNow();
            _updateUi?.Invoke();
        }

        public override void Save()
        {
            Settings.SaveToDisk();
        }

        private static void SaveToDiskNow()
        {
            Settings.SaveToDisk();
        }

        internal void RefreshUI()
        {
            _updateUi?.Invoke();
        }

        public override UIElement GetUI()
        {
            tContentPatch.Content.UI.UIStackPanel panel = new tContentPatch.Content.UI.UIStackPanel();
            panel.IsAutoUpdateSize = true;
            panel.ItemMargin = 2f;
            panel.Width.Set(0f, 1f);

            UIItemSwitch sw = new UIItemSwitch(null, "启用聊天翻译(收到消息即后台翻译存入历史, 快捷键开/关面板)");
            sw.SetVal(Settings.S.EnableChatTranslate);
            sw.OnValUpdate += v =>
            {
                Settings.S.EnableChatTranslate = v;
                Settings.SaveToDisk();
            };
            panel.Append(sw);

            UIItemTextBox key = new UIItemTextBox(Settings.S.TranslateKey, -1, null, "翻译快捷键(按一下开/关历史面板, XNA按键名如LeftControl/T)");
            key.OnLostFocus += () =>
            {
                Settings.S.TranslateKey = key.GetText();
                Settings.SaveToDisk();
            };
            panel.Append(key);

            UIItemTextBox api = new UIItemTextBox(Settings.S.ApiName, -1, null, "翻译API: CaiyunNoToken(彩云免Token, 免费免密钥, 国内可直连)");
            api.OnLostFocus += () =>
            {
                Settings.S.ApiName = api.GetText();
                Translators.TranslatorCore.UnloadCurrent();
                Settings.SaveToDisk();
            };
            panel.Append(api);

            UIItemTextBox lang = new UIItemTextBox(Settings.S.TargetLang, -1, null, "目标语言代码(默认zh中文, en英语/日ja等)");
            lang.OnLostFocus += () =>
            {
                Settings.S.TargetLang = lang.GetText();
                Settings.SaveToDisk();
            };
            panel.Append(lang);

            _updateUi = () =>
            {
                sw.SetVal(Settings.S.EnableChatTranslate);
                key.SetText(Settings.S.TranslateKey);
                api.SetText(Settings.S.ApiName);
                lang.SetText(Settings.S.TargetLang);
            };

            return panel;
        }
    }
}
