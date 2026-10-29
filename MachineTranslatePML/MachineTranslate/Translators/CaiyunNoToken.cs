using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MachineTranslate.Translators
{

    // 彩云小译免Token API(免费免密钥, 国内可直连); 多行文本按行逐条请求, 行间限速100ms
    public class CaiyunNoToken : Translator
    {
        private const string TranslatorUrl = "https://interpreter.cyapi.cn/v1/translator";
        private const string AuthorizationToken = "ssdj273ksdiwi923bsd9";

        private static int _requestSeq;

        // 同步HTTP放后台线程执行, 不占主线程
        public override void Translate(string text, string targetLang, Action<string> finishedCallback)
        {

            Task.Run(delegate
            {
                TranslateInner(text, targetLang, finishedCallback);
            });
        }

        private void TranslateInner(string text, string targetLang, Action<string> finishedCallback)
        {
            try
            {

                string result = "";
                string[] lines = text.Split('\n');
                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;

                    string lineResult = RequestOnce(line, targetLang);
                    if (lineResult == null)
                    {
                        FinishFailure(finishedCallback);
                        return;
                    }

                    result += lineResult + "\n ";
                    Thread.Sleep(100);
                }

                result = result.Trim();
                if (result.Length == 0)
                {
                    FinishFailure(finishedCallback);
                    return;
                }

                SetLookupValue(text, targetLang, result);
                TranslateStatus = Status.Idling;
                finishedCallback?.Invoke(result);
            }
            catch (Exception)
            {
                FinishFailure(finishedCallback);
            }
        }

        // 单行请求: source 数组化最小体 + JToken 双形态自适应解析(数组取[0]/字符串直接用); 失败返回null
        private static string RequestOnce(string text, string targetLang)
        {
            try
            {
                Dictionary<string, object> postData = new Dictionary<string, object>();
                postData["source"] = new string[] { text };
                postData["trans_type"] = "auto2" + targetLang;
                postData["request_id"] = GetRequestId();
                postData["detect"] = true;

                string jsonPayload = JsonConvert.SerializeObject(postData);

                HttpWebRequest request = Helper.CreateRequest(TranslatorUrl, "POST");
                request.Accept = "application/json";
                request.UserAgent = "caiyunInterpreter/5 CFNetwork/1404.0.5 Darwin/22.3.0";
                request.Headers.Add("x-authorization", "token " + AuthorizationToken);

                string responseJson = Helper.RequestText(request, jsonPayload);

                JToken root = JToken.Parse(responseJson);
                JToken target = root != null ? root["target"] : null;
                if (target == null) return null;

                if (target.Type == JTokenType.Array)
                {
                    JArray arr = (JArray)target;

                    if (arr.Count == 0) return text;
                    return arr[0].ToString();
                }

                if (target.Type == JTokenType.String)
                {
                    string s = target.ToString();
                    return s.Length == 0 ? text : s;
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetRequestId()
        {
            int seq = Interlocked.Increment(ref _requestSeq);
            return "mt" + Environment.TickCount.ToString("x8") + "-" + seq.ToString("x8");
        }
    }
}
