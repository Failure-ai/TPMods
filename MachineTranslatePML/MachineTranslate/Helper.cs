using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace MachineTranslate
{

    // 同步HTTP工具(HttpWebRequest, 6秒超时; 禁 async/HttpClient)
    public static class Helper
    {

        private const int RequestTimeoutMs = 6000;

        static Helper()
        {
            try
            {

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
            }
        }

        // 纯数字/标点/符号/控制字符视为不可译
        public static bool IsTranslatable(this string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.All(t => char.IsDigit(t) || char.IsControl(t) || char.IsPunctuation(t) || char.IsSymbol(t))) return false;
            return true;
        }

        public static HttpWebRequest CreateRequest(string url, string method)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = RequestTimeoutMs;
            request.ReadWriteTimeout = RequestTimeoutMs;
            return request;
        }

        public static string RequestText(HttpWebRequest request, string postBody)
        {
            if (postBody != null)
            {
                byte[] body = Encoding.UTF8.GetBytes(postBody);
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                using (Stream stream = response.GetResponseStream())
                {
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
        }
    }
}
