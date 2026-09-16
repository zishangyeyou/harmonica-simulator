# 第三方声明 / NOTICE

本项目自身不附带任何第三方库。编译与运行只依赖 Windows 自带的组件：

| 组件 | 用途 | 授权 |
|---|---|---|
| .NET Framework 4.x（`csc.exe`、`System.Windows.Forms`、`System.Drawing`） | 编译与界面 | Microsoft 随 Windows 提供 |
| Windows `winmm.dll` | MIDI 输出 | Microsoft 随 Windows 提供 |
| Windows `user32.dll` / `imm32.dll` / `kernel32.dll` | 输入模拟、热键、输入法检测 | Microsoft 随 Windows 提供 |

## 源码来源说明

本仓库的源代码为作者独立编写，未复制任何第三方项目的代码。

开发过程中参考过公开的技术资料与同类项目的**公开描述**（例如 Windows 官方
`SendInput` 文档、MIDI 文件格式规范说明），但没有引入其代码。

## 关于示例曲目

本仓库**不附带**任何 MIDI 或音频示例文件。
请使用你有权使用的曲谱自行测试。

## 关于商标

本仓库中可能提及的第三方名称均为其各自所有者的商标。
本项目与这些公司没有任何关联、合作或授权关系。
