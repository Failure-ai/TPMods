using System;
using System.Threading.Tasks;
using MachineTranslate.Translators;
using tContentPatch;
using tContentPatch.ModLoad;

namespace MachineTranslate
{

    // 模组主类(tPML 加载器自动发现并调用)
    public class ThisMod : Mod
    {

        // 版本号
        public const string ModVersion = "1.5.4";

        public static ModObject Mo = null;

        public override void Load(ModObject mo)
        {
            Mo = mo;

            TranslatorCore.Initialize();

            // 缓存加载放后台线程, 不阻塞启动
            Task.Run(delegate
            {
                CacheStore.Load();
            });
        }

        public override void Unload()
        {
            try { CacheStore.Save(); } catch (Exception) { }
            try { Settings.Settings.SaveToDisk(); } catch (Exception) { }

            TranslatorCore.Unload();
            Mo = null;
        }
    }
}
