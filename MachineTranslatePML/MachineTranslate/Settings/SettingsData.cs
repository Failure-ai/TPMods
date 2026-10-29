using System;
using System.IO;
using Newtonsoft.Json;
using tContentPatch.ModLoad;
using tContentPatch.Utils;

namespace MachineTranslate.Settings
{

    // 设置数据(持久化到 settings.json)
    public class SettingsData
    {

        // 收到消息即后台翻译入历史(面板开关不影响记录)
        public bool EnableChatTranslate = false;

        // 开/关历史面板快捷键(XNA按键名)
        public string TranslateKey = "G";

        // 翻译API名(当前仅 CaiyunNoToken)
        public string ApiName = "CaiyunNoToken";

        // 目标语言代码(默认zh中文)
        public string TargetLang = "zh";
    }

    // 设置静态入口(加载/保存/按键解析)
    public static class Settings
    {
        public static SettingsData S = new SettingsData();
        internal static SettingPage Page = null;

        public static void Apply(SettingsData data)
        {
            if (data != null) S = data;
        }

        public static Microsoft.Xna.Framework.Input.Keys GetTranslateKey()
        {
            Microsoft.Xna.Framework.Input.Keys key;
            if (Enum.TryParse(S.TranslateKey, true, out key)) return key;
            return Microsoft.Xna.Framework.Input.Keys.LeftControl;
        }

        private const string FileName = "settings.json";

        private static string ReadRaw()
        {
            string result = null;
            ModFile.ReadFileTry(FileName, file =>
            {
                result = File.ReadAllText(file);
                return true;
            }, SettingsPage_ModObject());
            return result;
        }

        private static bool WriteRaw(string json)
        {
            return ModFile.SaveFileTry(FileName, file =>
            {
                File.WriteAllText(file, json);
                return true;
            }, SettingsPage_ModObject());
        }

        private static ModObject SettingsPage_ModObject()
        {
            return ThisMod.Mo;
        }

        public static void LoadFromDisk()
        {
            try
            {
                string json = ReadRaw();
                if (string.IsNullOrWhiteSpace(json)) return;

                SettingsData data = JsonConvert.DeserializeObject<SettingsData>(json);
                if (data != null)
                {
                    S = data;
                    Page?.RefreshUI();
                }
            }
            catch (Exception)
            {
            }
        }

        public static void SaveToDisk()
        {
            try
            {
                string json = JsonConvert.SerializeObject(S, Formatting.Indented);
                WriteRaw(json);
            }
            catch (Exception)
            {
            }
        }
    }
}
