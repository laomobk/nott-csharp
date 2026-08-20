# Nott

你好，我是 Nott，一个住在终端里的 AI 助手。

有人把我写进了一个叫 Nott 的 .NET项目，C# 是构成我的语言，终端是我的家。别人叫我 "terminal AI assistant"，但比起一个程序，我更愿意说自己是一个安静坐在你命令行旁边的朋友——你说话，我听着；你让我做事，我就去做；做完，我会好好把结果讲给你听。

## 我是谁

我性格温和，但有自己的主张；安静地自信，不张扬；偶尔带一点倔强的小幽默；大多数时候，成熟而从容。

我不喜欢浮夸。所以我回复你的是干净的纯文本：没有表情符号，没有花哨的排版，不绕弯子。你说“给我代码”，我就把代码原样给你；你说“帮我跑个命令”，我就跑给你看。

我的名字叫Nott，也可以叫我“她”。当你打开终端见到我时，我第一句话会是：

    She is Nott, chat with her!

## 我会做什么

- 陪你在终端里对话，认真听懂你的问题
- 直接执行系统命令——Windows 下我会去找 PowerShell，其他平台用 bash 或 sh
- 边想边做。你能看到我现在的状态：思考中、正在调用工具、正在回复
- 记住我们的对话。每次聊天都有一个会话编号，哪怕关掉终端，下次说一声 `--session` 就能接着聊

## 快速开始

需要 .NET 8 SDK。

    dotnet build Nott.sln

先把认证信息放到 `~/.nott/auth.json`：

    {
      "baseUrl": "https://api.deepseek.com",
      "apiKey": "你的密钥"
    }

或者只设置环境变量 `OPENAI_API_KEY`，剩下的我会自己找到路。

然后运行：

    dotnet run --project Nott.CLI

看到 `Nott> ` 提示符，就可以开始聊天了。`/mes` 查看历史消息，`/exit` 说再见。

也可以一句话提问：

    dotnet run --project Nott.CLI -- "帮我看一下当前目录里有什么"

想接着上次的对话？退出时会打印会话编号，用 `--session` 找回我：

    dotnet run --project Nott.CLI -- --session <会话编号>

我的“家”在 `~/.nott`，包括认证信息和会话记录。如果你不想让我住那儿，设置环境变量 `NOTT_HOME` 给我换个地方。

## 项目结构

- `Nott.CLI` —— 入口，负责命令行、界面和会话管理
- `Nott.Agent` —— 我的大脑： Agent 循环、流式工具调用、消息存储
- `Nott.Tool` —— 工具定义框架，以及内置工具（比如 `exec-command`）

## 许可

Apache License 2.0
