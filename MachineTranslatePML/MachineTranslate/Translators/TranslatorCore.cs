using System;
using System.Collections.Generic;

namespace MachineTranslate.Translators
{

    public enum TransApi
    {
        CaiyunNoToken
    }

    // 翻译调度核心: 仅内置彩云免Token; 同一时刻只允许一个请求在途(在途时 TranslateTextLang 返回false)
    public static class TranslatorCore
    {
        public static readonly Dictionary<TransApi, Translator> Lookup = new Dictionary<TransApi, Translator>();

        private static Translator _current = null;

        public static void Initialize()
        {
            Lookup.Clear();
            Lookup[TransApi.CaiyunNoToken] = new CaiyunNoToken();
            _current = null;
        }

        public static void Unload()
        {
            Lookup.Clear();
            _current = null;
        }

        public static void UnloadCurrent()
        {
            _current = null;
        }

        public static Translator Current
        {
            get
            {
                if (_current != null) return _current;

                Translator t;
                if (Enum.TryParse(Settings.Settings.S.ApiName, true, out TransApi api) && Lookup.TryGetValue(api, out t))
                {
                    _current = t;
                    return _current;
                }

                if (Lookup.TryGetValue(TransApi.CaiyunNoToken, out t))
                {
                    _current = t;
                    return _current;
                }

                return null;
            }
        }

        // 目标语言(设置值, 空/非法回落 zh)
        public static string TargetLang
        {
            get
            {
                string lang = Settings.Settings.S.TargetLang;
                if (string.IsNullOrWhiteSpace(lang)) return "zh";
                return lang.Trim().ToLowerInvariant();
            }
        }

        public static bool TryGetCache(string text, out string value)
        {
            return Current.TryGetLookupValue(text, TargetLang, out value);
        }

        public static bool TryGetCacheLang(string text, string lang, out string value)
        {
            return Current.TryGetLookupValue(text, lang, out value);
        }

        public static bool TranslateText(string text, Action<string> finishedCallback)
        {
            return TranslateTextLang(text, TargetLang, finishedCallback);
        }

        public static bool TranslateTextLang(string text, string lang, Action<string> finishedCallback)
        {
            Translator translator;
            if (!Lookup.TryGetValue(TransApi.CaiyunNoToken, out translator)) return false;
            if (translator.TranslateStatus == Translator.Status.Translating) return false;

            translator.TranslateStatus = Translator.Status.Translating;
            translator.Translate(text, lang, delegate (string result)
            {
                if (finishedCallback != null) finishedCallback(result);
            });
            return true;
        }
    }
}
