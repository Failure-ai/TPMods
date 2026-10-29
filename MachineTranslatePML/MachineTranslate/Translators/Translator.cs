using System;
using System.Collections.Generic;

namespace MachineTranslate.Translators
{

    // 翻译器基类: 译文查找表(key=目标语言+"-"+原文, 加锁) + 状态机; 回调恰好一次(失败回调null)
    public abstract class Translator
    {
        public enum Status
        {
            Idling,
            Translating,
            AttemptFailed
        }

        private readonly object _lock = new object();
        private Dictionary<string, string> _lookup = new Dictionary<string, string>();
        internal volatile Status TranslateStatus = Status.Idling;

        public abstract void Translate(string text, string targetLang, Action<string> finishedCallback);

        public void SetLookupRaw(Dictionary<string, string> lookup)
        {
            lock (_lock)
            {
                _lookup = lookup ?? new Dictionary<string, string>();
            }
        }

        public Dictionary<string, string> GetLookupRaw()
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_lookup);
            }
        }

        public void SetLookupValue(string key, string targetLang, string value)
        {
            lock (_lock)
            {
                _lookup[targetLang + "-" + key] = value;
            }
        }

        public bool TryGetLookupValue(string key, string targetLang, out string value)
        {
            lock (_lock)
            {
                return _lookup.TryGetValue(targetLang + "-" + key, out value);
            }
        }

        // 失败收尾: 复位状态并回调null(回调恰好一次)
        protected void FinishFailure(Action<string> finishedCallback)
        {
            TranslateStatus = Status.Idling;

            try
            {
                if (finishedCallback != null) finishedCallback(null);
            }
            catch (Exception)
            {
            }
        }
    }
}
