using System;
using System.Collections.Generic;
using System.IO;
using MachineTranslate.Translators;
using Newtonsoft.Json;
using tContentPatch.Utils;

namespace MachineTranslate
{

    // 译文缓存持久化(cache.json: API名 -> (目标语言-原文 -> 译文))
    public static class CacheStore
    {
        private const string FileName = "cache.json";

        public static void Load()
        {
            try
            {
                string json = null;
                ModFile.ReadFileTry(FileName, delegate (string file)
                {
                    json = File.ReadAllText(file);
                    return true;
                }, ThisMod.Mo);

                if (string.IsNullOrWhiteSpace(json)) return;

                Dictionary<string, Dictionary<string, string>> all =
                    JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(json);
                if (all == null) return;

                foreach (KeyValuePair<string, Dictionary<string, string>> pair in all)
                {
                    TransApi api;
                    if (!Enum.TryParse(pair.Key, true, out api)) continue;

                    Translator translator;
                    if (TranslatorCore.Lookup.TryGetValue(api, out translator))
                    {
                        translator.SetLookupRaw(pair.Value);
                    }
                }
            }
            // 缓存损坏: 删除重建
            catch (JsonException)
            {
                try
                {
                    ModFile.SaveFileTry(FileName, delegate (string file)
                    {
                        File.Delete(file);
                        return true;
                    }, ThisMod.Mo);
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Save()
        {
            try
            {
                Dictionary<string, Dictionary<string, string>> all = new Dictionary<string, Dictionary<string, string>>();
                foreach (KeyValuePair<TransApi, Translator> pair in TranslatorCore.Lookup)
                {
                    all[Enum.GetName(typeof(TransApi), pair.Key)] = pair.Value.GetLookupRaw();
                }

                string json = JsonConvert.SerializeObject(all, Formatting.Indented);

                ModFile.SaveFileTry(FileName, delegate (string file)
                {
                    File.WriteAllText(file, json);
                    return true;
                }, ThisMod.Mo);
            }
            catch (Exception)
            {
            }
        }
    }
}
