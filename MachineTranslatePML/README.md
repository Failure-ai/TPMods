# 机器翻译 (MachineTranslate) — tPML 模组

Terraria 1.4.5 tPML 聊天翻译模组：收到聊天消息自动后台翻译成中文存入历史，按快捷键开/关面板对照查看；发送框输入中文停 1 秒自动译成英文，点按钮发送。

## 功能

- **收消息自动翻译**：任意语言 → 中文（目标语言可改），译文存入临时历史，与原文对照显示
- **历史面板**：快捷键（默认左 G）开/关；滚轮 / 拖动滚动条翻阅；面板可拖动；带发送输入框
- **发送翻译**：面板发送框输入中文，停 1 秒自动译成英文，点「发送」按钮发出（单人/多人均支持）
- **翻译引擎**：仅内置彩云小译免 Token API（免费免密钥，国内可直连）；译文本地缓存；失败自动退避重试
- **兼容 OptimizeAndTool**：清除重复聊天把刷屏合并成「原文 xN」时，面板同文只占一条、只调一次 API
- **设置**：模组设置 → 机器翻译，可改快捷键 / 目标语言 / API

## 构建

环境：Visual Studio + .NET Framework 4.5 目标（csproj 为自包含非 SDK 风格，C# 8.0）。

1. 解压后用 VS 打开 `MachineTranslate.slnx`；
2. 按你电脑上的路径修改 csproj 里的项目引用（tPML 根目录与游戏目录下的 dll/exe）；
3. 生成解决方案 —— Debug/Release 输出路径需要修改；
4. 重启 tPML 加载模组。

**确认游戏里跑的是新版**：v1-beta16-t1.4.5.8，泰拉瑞亚1.4.5.8。

## 使用发布版本
- 下载发布版本：[MachineTranslate.zip](https://github.com/Failure-ai/VideoParsing/releases)
- 解压文件到Mods目录，启用加载器即可运行

## 目录结构

```
Mods/MachineTranslate/
├── MachineTranslateMod.cs    模组入口(版本号/加载日志)
├── Hooks/ChatTranslate.cs    主体: 消息入史/翻译排队/历史面板UI
├── Translators/              翻译器(彩云免Token)与调度核心
├── Helper.cs                 同步HTTP工具
├── CacheStore.cs             译文缓存持久化(cache.json)
├── Settings/                 设置数据与中文设置页
└── info.json / loadConfig.json / ico.png
```

## 📄 致谢

- 参考模组：[Cyrillya/MachineTranslate](https://github.com/Cyrillya/MachineTranslate)（Cyrilly）
- 免密钥 API：[akl7777777](https://github.com/akl7777777)
- 加载器：[github-user-64/tPlainModLoader](https://github.com/github-user-64/tPlainModLoader)
