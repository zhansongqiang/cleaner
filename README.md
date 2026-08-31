# ClearC — C盘清理工具

> 零依赖、单文件、开箱即用的 Windows 磁盘清理工具。
> **v1.1.0** · Copyright (c) **zsqstudio** · 联系：**11016795@qq.com**

![主界面](docs/screenshot-main.png)

## 为什么是 ClearC？

- **零依赖**：不用安装 .NET SDK、不用 IDE、不用 NuGet —— 用 Windows 系统自带的 C# 编译器一条命令编译
- **单文件**：整个工具就是一个 `ClearC.exe`（约 60KB），拷走就能用
- **可兜底**：所有删除操作分级处理 —— 安全级默认勾选、需确认级默认不勾选；重复文件/批处理页的删除进**回收站**，随时可反悔

## 功能一览

| 模块 | 功能 |
|---|---|
| 🧹 **缓存清理** | 22+ 类可清理项（系统临时/更新残留/开发缓存 npm·gradle·pip/浏览器缓存/WPS 备份/网盘·微信缓存等），按 安全/需确认 分级，支持一键勾选安全项、批量删除 |
| 🔍 **重复文件对比** | 按内容找出重复文件并分组，可按策略（保留每组最新/最旧/路径最短等）自动勾选，删除进回收站 |
| 📦 **文件批处理** | 按类型/大小/日期条件批量搜索，支持批量删除（回收站）与批量移动 |
| 📊 **大文件TOP** | 找出占用最大的 50 个文件 + 20 个文件夹，一眼定位空间大户 |
| ℹ️ **关于** | 版本与版权信息 |

**扫描范围**支持：`整个电脑（所有本地硬盘）` / 指定硬盘 / 用户目录 / 下载目录 / 自定义路径 —— **选中范围即自动开始扫描**，无需再点按钮；扫描过程状态栏实时显示进度。

![大文件TOP](docs/screenshot-top.png)

## 编译

双击 `build.bat`，或手动执行：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /nologo /target:winexe /platform:anycpu /codepage:65001 /out:ClearC.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll ClearC.cs
```

- 源码仅一个文件 `ClearC.cs`（C# 5 语法，兼容 .NET Framework 4.0+ 自带编译器）
- 也可直接从 [Releases](../../releases) 下载编译好的 `ClearC.exe`

## 使用提示

- 「缓存清理」页的删除为**直接删除**（立即释放空间，回收站也占 C 盘）；「重复文件/批处理」页的删除走**回收站**
- 需确认项（微信/WPS/网盘缓存等）涉及个人数据，默认不勾选，请自行判断
- 清理 `Windows 更新下载缓存`、`Windows 错误报告` 等系统目录需要管理员权限（右键 → 以管理员身份运行）
- 大文件 TOP 榜中的 `hiberfil.sys`（休眠文件）若不需休眠功能，可管理员运行 `powercfg /h off` 释放

## 系统要求

- Windows 10 / 11（64 位）
- .NET Framework 4.0+（系统自带，无需安装）

---

⚠️ **免责声明**：清理工具会删除文件，请先确认勾选项再执行；重要数据请提前备份。使用本工具造成的任何数据损失，作者不承担责任。

Copyright (c) 2026 zsqstudio · 11016795@qq.com · All Rights Reserved
