# MyBigBlueFish · 蓝色大肥鱼桌宠 / Big Blue Fish Desktop Pet

**[中文](#中文) · [English](#english)**

<p align="center">
  <img src="docs/images/bigbluefish-rest-transparent.png" width="38%" alt="蓝色大肥鱼休息状态 / Big Blue Fish resting" />
  <img src="docs/images/bigbluefish-speaking-transparent.png" width="58%" alt="蓝色大肥鱼吐字状态 / Big Blue Fish speaking" />
</p>

---

## 中文

> **关于本仓库**：MyBigBlueFish 基于 [Bai-Cute/big-blue-fish-desktop-pet](https://github.com/Bai-Cute/big-blue-fish-desktop-pet) 的 v1.0.5（Apache License 2.0）修改，在原版基础上新增了「沿屏幕四周爬行」和「陪伴增强包」。原作者的提交历史、版权与许可全部保留；新增和修改的内容见 [docs/COMPANION_PLUS.md](docs/COMPANION_PLUS.md)。

蓝色大肥鱼是一款住在电脑桌面上的 AI 桌宠软件，支持 Windows 与 Linux 双平台。她是一位蓝发的小鲸鱼娘，形象来自对 DeepSeek 的二创虚拟形象“蓝色大肥鱼”。软件内置 4B 本地大模型，借助 OCR 技术读懂主人正在桌面上做的事情，每隔一段时间就从文字气泡里送来一句可爱的话，陪伴和关心主人；她还会查看天气、浏览科技新闻，再用自己的方式讲给主人听。

鱼鱼准备了一百多种可爱的动作表情，平时会在桌面上呼吸、哼歌、伸懒腰、吃零食、玩耍，到了饭点会认真吃饭，到了节日会换上应景的小活动，被点一下、被拎起来时也有专门设计的反应。每次启动，她都会提起裙摆向主人行礼请安；关闭时，也会用同样的礼仪和主人道别。

### 本仓库新增的功能

以下功能只在 Windows 版中提供，标准版和无MCP版都可以使用；设置集中在「陪伴与外观」和新增的「互动与关怀」页面。所有新数据只保存在本机安装目录的 `data\` 中，不上传。

- **沿屏幕四周爬行**：鱼鱼用「螃蟹走路」沿屏幕可用区域四周横着爬——底边站立、左右贴墙、顶边倒挂，拐角处顺势转身。可选关闭 / 偶尔 / 经常 / 一直爬（巡游），速度慢 / 中 / 快；点她会停下回应，拖走就停止。
- **双击聊天**：双击鱼鱼弹出输入框，内置本地模型以鱼鱼的口吻多轮对话。
- **会淡忘的记忆**：聊过的内容保存在本机，并像人一样慢慢淡忘——重要的事记得久、越常提起越牢；久远的只剩模糊印象，细节会缺失、时间会记错，最后彻底忘掉。说「叫我××」她就会这样称呼你。
- **摸头 / 戳戳 / 喂零食**：鼠标在她头上来回轻划就是摸头；连戳会傲娇生气；右键可以喂六种零食。
- **好感度与心情**：互动和陪伴时长会增加好感，好感越高她越黏人；心情随时间、饭点和互动变化。离开电脑回来时她会迎接你，很多天没见会说想你。
- **关怀提醒**：久坐、喝水、护眼提醒（按实际使用时间计算）；每天的早安、深夜催睡和晚安。
- **番茄钟**：右键「专注」，专注期间她安静陪伴，结束时叫你休息。
- **定时提醒**：右键「提醒事项」，或在聊天里说「30分钟后提醒我开会」「明天早上8:30叫我起床」，重启后仍然保留。
- **游戏陪玩**：识别常见游戏后，全屏也不躲起来、鼠标点击会穿过她和她的气泡（切到游戏立即生效，不会挡操作）、不占用显卡，偶尔加油、每小时提醒休息；可选识别「胜利 / 失败」画面并做出反应。
- **置顶 / 不置顶**：右键或设置里切换；不置顶时她待在其他窗口后面，点她或托盘图标叫到前面，有提醒时暂时跳到最前。
- **透明度**：设置里拖动滑块让鱼鱼变得半透明（气泡保持清晰），鼠标移上去时恢复不透明。
- **三角洲里模仿你**：你开枪她举水枪开火、你开镜她出现准星、你受伤她红闪一抖、你缠绷带 / 打药 / 吃药她也跟着做（只读取按键和画面，不碰游戏）。
- **三角洲行动 · 烽火地带**：本机识别画面上的进图、撤离成功、被击倒和阵亡，配上专属动作和吐槽（放烟花、钱袋满溢、袋空如洗、气急败坏……）；记录每天和累计的战绩、连撤 / 连寄、带出的哈夫币，收工时帮你复盘；聊天时懂摸金、跑刀、大红这些黑话，也可以直接告诉她「撤离成功了」「寄了」。

### 获取与构建陪伴增强版

本仓库没有单独发布安装包。推荐做法：

1. 从原作者的 [GitHub Releases](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/releases/tag/v1.0.5) 安装官方 v1.0.5（Windows 版，标准版或无MCP版均可）；
2. 在 Windows x64 上安装 .NET 10 SDK，于本仓库根目录编译（无MCP版示例，标准版去掉 `-p:CompanionEdition=Safe`）：

   ```powershell
   dotnet build ./code/BigBlueFish.Windows/BigBlueFish.Windows.csproj -c Release -p:Platform=x64 -p:CompanionEdition=Safe -p:CompanionInputMode=Ocr -p:RestoreLockedMode=true -p:InformationalVersion=1.0.5-companion.5
   ```

3. 退出鱼鱼，备份安装目录中的 `BigBlueFish.dll` 和 `BigBlueFish.pdb`，再用 `code/BigBlueFish.Windows/bin/x64/Release/net10.0-windows10.0.26100.0/` 里的同名文件替换，重新启动即可。设置页左侧显示 `1.0.5-companion.5` 即为增强版。

应用内「检查更新」升级到官方新版本时，会覆盖增强版的程序文件。

<p align="center">
  <img src="docs/images/bigbluefish-maid-bow.gif" width="32%" alt="女仆屈膝礼仪" />
  <img src="docs/images/bigbluefish-idle-breath.gif" width="32%" alt="待机呼吸休闲" />
  <img src="docs/images/bigbluefish-click-wave.gif" width="32%" alt="点击回应元气挥手" />
</p>

### 认识鱼鱼

鱼鱼有一头柔软的蓝色长发，头顶戴着蕾丝女仆发饰，耳边是小小的鲸鳍，身后摇着一条鲸鱼尾巴。她穿着深蓝色的女仆装，白色围裙上印着一只小鲸鱼，整个人看起来就像一位住进电脑里的小鲸鱼娘。她平时安静地待在桌面边缘，身边透明的地方可以直接点到后面的桌面，主人也可以把她拖到任何喜欢的位置，大小可以在 50% 到 200% 之间调整。

她说话的语气亲切温柔，又带着一点可爱俏皮。看见主人在写代码，她会摇摇尾巴为认真工作的主人加油；看见主人和朋友约明天吃饭，她会忍不住对好吃的动一点小心思；天气变冷或者主人忙了很久，她也会轻轻地送来一句关心。启动时，她会按照时间说“主人早上好，鱼鱼给主人请安啦～”；道别时，她会说“主人再见～鱼鱼喜欢主人哦”。

### 她怎样陪伴主人

鱼鱼会读取当前前台窗口的画面，通过 OCR 识别其中的文字，并按照标题栏、侧边栏、正文等区域整理好，再交给内置的 4B 本地模型理解。无论主人在读文章、写文档、改代码，还是在聊天里提到了什么话题，她都能顺着眼前的内容组织一小段新的话；主人停在桌面、主页或新标签页时，她也会自然地问候、撒娇或者碎碎念。Windows 使用系统自带的 Windows OCR，Linux 使用 Tesseract，X11 与 Wayland 桌面均可使用。

启动约 45 秒后，鱼鱼会第一次开口，之后在主人选择的时间范围内随机安排下一次说话，范围从 2–3 分钟到 20–30 分钟共六档。她会挑主人暂时停下键盘和鼠标的间隙开口，准备说话时头边会转起一个蓝色的小圈圈，随后文字一个字一个字地出现在奶油色的气泡里。气泡大小和停留时间都可以在设置中调整，右键菜单里的“吐字测试”可以随时让她现场说一句。

主人可以按省、市、区县三级选择天气地点。鱼鱼每小时会悄悄更新一次天气，播报时结合气温、体感、湿度、风力、最高最低温和降水概率，用鲸鱼娘的口吻讲给主人听；右键菜单里的“天气”可以随时让她汇报。她也会从 IT之家浏览最新的科技新闻，挑一条讲清楚要点，再加上一小句自己的感受。

这些自动说的话每次都是一次单独的输出，没有对话历史；读屏内容只在本机交给内置模型使用，不会被上传到云端，也不会保存到本地。（本仓库新增的双击聊天另有自己的本地对话记忆，可在设置中关闭或清空。）

笔记本电脑离开电源时，鱼鱼可以自动停止模型推理并把模型移出内存，帮主人照顾续航；外部程序全屏时，她会自动躲起来，结束后再回到桌面（玩游戏时可选择让她留下来陪玩）。这些行为都可以在设置中按自己的习惯调整。

### 一百多种动作表情

鱼鱼内置 106 段透明动画，覆盖待机、点击、拖动、日常事件、用餐、季节和节日等场景。每一种动作都有自己的出现权重，并会结合当前时间、日期、季节、饭点和主人的操作实时计算，这些概率都经过细致严谨的微调，让她的表情丰富有趣，又贴合主人当下的生活节奏。每段动作在没有新的交互打断时都会完整播放，再自然地衔接到下一段。

<p align="center">
  <img src="docs/images/bigbluefish-meal.gif" width="32%" alt="用餐动作" />
  <img src="docs/images/bigbluefish-fireworks.gif" width="32%" alt="节日烟花动作" />
  <img src="docs/images/bigbluefish-sleep.gif" width="32%" alt="夜间小憩沉眠" />
</p>

待机时，鱼鱼会在好几种安静的动作之间轮换，轻轻地呼吸、悠闲地哼歌；到了晚上 23 点之后，她会开始犯困，时不时打个小盹，直到第二天早上 8 点。主人点击她时，她会从好几种回应里挑一种，可能元气地挥挥手，可能害羞地吓一跳，也可能露出别的小表情，有些回应要多点几次才容易遇到。把她拎起来拖动时，她会悬在半空中保持被抓住的小模样，直到主人松手。

每隔一段时间（默认 40 秒，可在 30 秒到 5 分钟之间调整），鱼鱼会从大量日常事件中挑一件小事来做：跳舞、吹泡泡、敲小电脑，等等许多许多精美华丽的动作。三餐的时间可以在设置中按主人的作息填写，饭点前后一小时里，她会更常端出好多种美食和小吃。早上她更容易打哈欠，每天晚上 23 点后有空的时候，还会认真地刷一次牙。

节日是鱼鱼特别用心准备的部分。春节、元宵、情人节、520、端午、七夕、中秋、万圣节、圣诞节等节日前后，她都准备了专属的节日动作，比如春节写福字、放烟花，中秋赏月吃月饼，有些动作还会随着节日临近越来越常见；节日前后的饭点，她也会端出应景的节日美食。农历节日按照农历日期计算，闰月也能正确处理。

季节也会改变她的日常：冬天会堆雪人，秋天会被落叶埋住，夏天还有好几种消暑的小活动。她偶尔也会跑一跑、学螃蟹横着走，跑出一小段距离，在那里待一会儿，再回到原来的位置。

除了这些，鱼鱼还藏着许多没有写在这里的小心思。愿主人在每天的陪伴里慢慢发现她的可爱，每一次不经意的抬头，都能撞见一个小小的惊喜。

### 安装（原作者的官方版本）

官方安装包请从原作者的 [GitHub Releases](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/releases/latest) 下载对应系统的版本。每个平台都提供标准版和无MCP版，文件名中带 `Safe` 的是无MCP版，各安装包都附有配套的 `.sha256` 校验文件。

| 平台 | 标准版 | 无MCP版 |
| --- | --- | --- |
| Windows x64 | `BigBlueFish-v1.0.5-OCR-Setup.exe` | `BigBlueFish-v1.0.5-Safe-OCR-Setup.exe` |
| Ubuntu／Debian x64 | `BigBlueFish-v1.0.5-Linux-x64.deb` | `BigBlueFish-v1.0.5-Safe-Linux-x64.deb` |

在 Windows 上，双击安装器即可开始部署。安装器会把桌宠程序、动画资源、.NET 运行时和本地推理运行时一并安装好，默认位置是 `Program Files` 下的“蓝色大肥鱼”文件夹，并创建桌面和开始菜单快捷方式。随后安装器会请主人选择模型的准备方式：Hugging Face（推荐）、国内源（稳定）或手动安装。自动下载的模型约 2.7 GB，下载完成后会经过 SHA-256 校验，中途断开也能从已有进度继续。下载时如果想改用手动安装，可以选择“放弃并转为手动安装”；手动安装会显示模型应放入的文件夹，并打开模型来源页面，也可以直接选择已经下载好的 GGUF 文件。卸载时可以在卸载界面勾选“保留模型和设置”。

在 Ubuntu／Debian 上，下载对应的 `.deb` 后，在其所在目录执行：

```bash
sudo apt install ./BigBlueFish-v1.0.5-Linux-x64.deb
```

安装无MCP版时换成对应的文件名即可。安装完成后，可以从应用菜单或 `bigbluefish` 命令启动鱼鱼，再到设置的“模型与信息”中点击“安装模型”，为她准备本地模型。

如果暂时不想下载模型，也可以先让鱼鱼以无模型模式住进桌面。这时她依然会行礼、问候、道别，播放全部动作表情，回应主人的点击和拖动。之后随时可以到设置中点击“安装模型”，让她开始读懂屏幕、开口说话。

鱼鱼可以在设置中检查更新，也可以在每次启动时自动检查。发现新版本后，应用会下载并校验对应版本的安装包，完成更新后重新启动，模型和设置都会继续保留。Windows 用户也可以退出桌宠后手动运行新版安装器，选择原安装目录完成更新。

### 内置模型

鱼鱼使用 Qwen3.5-4B 的 Q4_K_M 量化 GGUF 模型，由随包分发的 [llama.cpp](https://github.com/ggml-org/llama.cpp)（b10809，Vulkan 版本）在本机推理，可以使用 GPU 加速，也可以切换到 CPU 运行。两个预设来源的模型有所不同，但都适合本应用，主人可以按自己的网络环境选择。

Hugging Face 来源为 [Biomanticus/Qwen3.5-4B-heretic-gguf](https://huggingface.co/Biomanticus/Qwen3.5-4B-heretic-gguf/tree/b63ff4662e4863cfa005c9cd8ed34b87ed44b7e1)，它是 [p-e-w/Qwen3.5-4B-heretic](https://huggingface.co/p-e-w/Qwen3.5-4B-heretic) 的 GGUF 转换版本，基于 Qwen3.5-4B。国内来源为 ModelScope 上的 [diodel/Qwen3.5-4B-Q4_K_M-GGUF](https://modelscope.cn/models/diodel/Qwen3.5-4B-Q4_K_M-GGUF/files)，是原版 [Qwen/Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B) 的 Q4_K_M 转换版本。两个来源都使用固定修订，具体文件名、大小与校验值见[第三方声明](code/THIRD-PARTY.md)。

模型会根据当前设备的可用显存自动调整计算缓冲区，并由 llama.cpp 自动分配模型层，小显存设备也能顺利加载；GPU 初始化失败时，鱼鱼会自动切换到 CPU 兼容模式，并在设置中显示实际的运行模式。

### 连接桌面 Agent（MCP）

从 1.0.0 起，标准版的蓝色大肥鱼提供一个标准 MCP 接口，可以连接一个桌面 AI Agent，并接受它的命令。连接之后，主人可以在 AI Agent 端自行配置相关的逻辑和能力，实现电脑操控、与云端大模型对话、自定义动作逻辑等各种自定义功能。

连接方法：在设置的“Agent 与 MCP”页面中打开“开启 MCP 接口”，阅读并确认弹出的权限说明，再点击“复制 MCP 连接配置”，把复制到的内容填进 Agent 的 MCP 服务器配置中即可。配置指向随包安装的桥接程序，形如：

```json
{
  "mcpServers": {
    "big-blue-fish": {
      "command": "C:\\Program Files\\蓝色大肥鱼\\BigBlueFish.Mcp.exe",
      "args": []
    }
  }
}
```

Linux 上的 `command` 为 `/opt/bigbluefish/BigBlueFish.Mcp`。桥接程序会连接到正在运行的鱼鱼，连接成功后，设置页面会显示当前的连接数量。

连接后，Agent 可以使用以下能力：

- 读取和修改鱼鱼的设置，查看她当前的动作、队列和模型状态；
- 获取当前前台窗口或整个屏幕的截图；
- 查询全部 106 个动作，让鱼鱼立即播放某个动作，或者把动作排进队列依次播放；
- 让鱼鱼立即说出一段指定的话，或者在下一次正常说话时机说出来；
- 实时接收主人点击、拖动鱼鱼的交互事件。

如果希望完全交给 Agent 来安排，可以开启“提线木偶模式”。这时鱼鱼会暂停自己的说话和动作切换，安静地循环播放“待机呼吸休闲”，等待 Agent 指定动作和文字。开启“双击与鱼鱼对话”后，只要有 Agent 已经连接，双击鱼鱼就会把输入交给 Agent；没有 Agent 连接时，本仓库的双击会打开与内置模型的本地聊天。

**风险提醒：** 通过 MCP 连接后，截屏会被传递给 MCP 彼端，其处理方式由彼端应用决定，可能导致主人的信息被上传或保存。MCP 的权限对整个接口统一生效，开启后每个已连接的 Agent 都可以使用上面列出的全部能力。MCP 接口默认关闭，开启前软件会弹窗说明这些权限，请只连接你信任的 Agent。完整说明见 [MCP 接入参考](docs/MCP.md)。

### 无MCP版

为了彻底消除 MCP 可能带来的隐私风险，蓝色大肥鱼为 Windows 和 Linux 额外提供了无MCP版。无MCP版在编译时就移除了 MCP 控制服务、桥接程序、Agent 输入框和相关设置页面，只保留鱼鱼自己的陪伴功能。安装包文件名中带有 `Safe` 字样。

### 从仓库构建

仓库面向开发者和维护者，包含源码、动画资源、地区数据、构建与发布脚本、测试程序、依赖锁文件和许可文件。模型与推理运行时由脚本另行准备。在 Windows x64 上安装 .NET 10 SDK 后，可以从仓库根目录构建：

```powershell
# 标准版
./code/build.ps1 -Locked -InputMode Ocr

# 无MCP版
dotnet build ./code/BigBlueFish.Windows/BigBlueFish.Windows.csproj -c Release -p:Platform=x64 -p:CompanionEdition=Safe -p:RestoreLockedMode=true
```

准备模型时，可以用模型脚本从任一来源下载，或导入本机已有的 GGUF 文件，然后指定 llama.cpp 运行时目录和模型目录启动；不准备模型时，用 `-NoModel` 以无模型模式启动：

```powershell
./tools/Setup-Model.ps1 -Source HuggingFace -DestinationDirectory "D:\BigBlueFish-local"
./tools/Setup-Model.ps1 -Source ModelScope -DestinationDirectory "D:\BigBlueFish-local"
./tools/Setup-Model.ps1 -Source Manual -ModelFile "D:\下载\我的模型.gguf" -DestinationDirectory "D:\BigBlueFish-local"

./code/run.ps1 -InputMode Ocr -RuntimeDirectory "推理运行时目录" -ModelsDirectory "D:\BigBlueFish-local\models"
./code/run.ps1 -InputMode Ocr -NoModel
```

生成安装包需要一个已经核对过的 llama.cpp 运行时目录（Windows 使用 b10809 的 `win-vulkan-x64` 版本）。Linux 安装包在 WSL 中的 Ubuntu／Debian 环境里构建，需要 gcc、pkg-config、libdbus-1-dev、dpkg-deb 和 FFmpeg：

```powershell
# Windows 安装器，-Edition Safe 生成无MCP版
./tools/Build-Installer.ps1 -Edition Standard -RuntimeDirectory "Windows运行时目录"

# Ubuntu／Debian x64 安装包，-Edition Safe 生成无MCP版
./tools/Build-LinuxPackage.ps1 -Distribution "WSL发行版名称" -RuntimeDirectory "Linux运行时目录" -Edition Standard
```

项目版本统一记录在根目录的 `Version.props`。源码结构、修改入口、测试方式见[开发说明](code/README.md)。

### 文档与反馈

- 本仓库的新增与修改：[docs/COMPANION_PLUS.md](docs/COMPANION_PLUS.md)
- 原版的版本变化：[CHANGELOG.md](CHANGELOG.md)；完整行为规格：[功能规格](docs/AGENT_FUNCTIONAL_SPEC.md)、[版本规格目录](docs/specs/README.md)
- 关于增强版的问题请在本仓库的 Issues 中反馈；关于原版的问题请到[原作者仓库](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/issues)反馈。

### 开源许可与第三方权利信息

本项目以 [Apache License 2.0](LICENSE) 发布，原始版权归原作者所有；本仓库的修改同样以 Apache License 2.0 发布，修改内容见 [docs/COMPANION_PLUS.md](docs/COMPANION_PLUS.md) 和提交历史。鱼鱼的形象来自对 DeepSeek 的二创虚拟形象“蓝色大肥鱼”；项目使用的动画、模型、运行时、字体和数据分别来自以下项目，各自遵循其原有许可：

- 角色动画：106 段透明 WebM 动画来自 [PC2005-cloud/dsh-pet](https://github.com/PC2005-cloud/dsh-pet)，MIT 许可，保留原有颜色、透明度与 24 帧时序，动作的选择与触发逻辑为本项目定制；
- 本地模型：Qwen3.5-4B 及其衍生的 heretic 版本与 GGUF 转换，Apache-2.0 许可，模型文件由用户另行下载；
- 推理与运行环境：llama.cpp（MIT）、.NET 10 运行时（MIT）、Microsoft Visual C++ 运行库（Microsoft 再分发条款）；
- 动画解码：Windows 版使用 FFmpeg 9.0.1（GPL-3.0-or-later），作为独立进程运行；Linux 版使用系统的 FFmpeg；
- Linux 界面：Avalonia、MicroCom.Runtime、Tmds.DBus.Protocol、SkiaSharp、HarfBuzzSharp（MIT）；
- 文字识别：Windows 版使用系统提供的 Windows.Media.Ocr，Linux 版使用 Tesseract；
- 气泡字体：乐米小奶泡体，按其公布的免费商用条款内嵌；
- 天气数据：[Open-Meteo](https://open-meteo.com/)，CC BY 4.0；科技新闻：[IT之家 RSS](https://www.ithome.com/rss/)；
- 行政区划数据：[AreaCity-JsSpider-StatsGov](https://github.com/xiangyuecn/AreaCity-JsSpider-StatsGov)，MIT 许可，仅内嵌层级与近似中心坐标。

各组件的版本、修订、校验值和版权说明见[第三方声明](code/THIRD-PARTY.md)，完整许可文本见[许可文件目录](code/licenses)。

---

## English

> **About this repository**: MyBigBlueFish is a modified version of [Bai-Cute/big-blue-fish-desktop-pet](https://github.com/Bai-Cute/big-blue-fish-desktop-pet) v1.0.5 (Apache License 2.0). On top of the original it adds **edge crawling** and a **companionship pack**. The original author's commit history, copyright and license are kept in full; everything added or changed is listed in [docs/COMPANION_PLUS.md](docs/COMPANION_PLUS.md) (in Chinese).

Big Blue Fish is an AI desktop pet that lives on your desktop, for Windows and Linux. She is a little blue-haired whale girl, based on "Big Blue Fish" (蓝色大肥鱼), a fan-made character inspired by DeepSeek. The app ships with a local 4B language model and uses OCR to understand what you are doing on screen; every so often she says something cute in a speech bubble to keep you company and look after you. She also checks the weather and browses tech news, and tells you about them in her own way.

She has more than a hundred animations: breathing, humming, stretching, snacking and playing while idle, eating properly at mealtimes, special activities for festivals, and dedicated reactions when clicked or picked up. Every time she starts she lifts her skirt and curtsies to greet you, and she says goodbye the same way when closed.

### What this repository adds

These features are Windows-only and work in both the Standard and the no-MCP (Safe) editions. Their settings are on the "陪伴与外观" (Companion & Appearance) page and on the new "互动与关怀" (Interaction & Care) page. All new data stays on your computer in the `data\` folder of the installation and is never uploaded.

- **Edge crawling**: she crab-walks around the usable area of the screen — standing on the bottom edge, clinging to the left and right walls, hanging upside down along the top, and turning at each corner. Modes: off / sometimes / often / always (patrol); speed: slow / medium / fast. Clicking makes her stop and react; dragging her stops the crawl.
- **Double-click chat**: double-click her to open a small input box and chat with her through the local model, in her own voice, over several turns.
- **Memory that fades**: conversations are stored locally and fade like human memory — important things last longer and get stronger each time they come up; old memories become vague, lose details, get the time wrong, and are eventually forgotten. Tell her "叫我××" ("call me ××") and she will use that name.
- **Pat, poke and feed**: moving the mouse back and forth over her head pats her; poking her repeatedly makes her grumpy; the right-click menu lets you feed her six kinds of snacks.
- **Affection and mood**: interactions and time spent together raise affection, and the higher it is the more affectionate she gets; her mood changes with the time of day, mealtimes and your interactions. She welcomes you back when you return to the computer and tells you she missed you after a long absence.
- **Care reminders**: reminders to stand up, drink water and rest your eyes (based on actual computer use); good morning, late-night "go to bed" nudges and good night.
- **Focus timer**: right-click "专注" (Focus) for a pomodoro; she keeps you quiet company and tells you to take a break when it ends.
- **Timed reminders**: right-click "提醒事项" (Reminders) or say in the chat "30分钟后提醒我开会" (remind me about the meeting in 30 minutes) or "明天早上8:30叫我起床" (wake me up at 8:30 tomorrow); reminders survive restarts.
- **Game companion**: when a common game is in front she stays visible in fullscreen, lets every mouse click pass through her and her speech bubble (from the moment you switch to the game), keeps the GPU free, cheers now and then and suggests a break every hour; optionally she can recognise victory / defeat screens and react.
- **Always on top or not**: switch it from the right-click menu or in settings. When she is not on top she stays behind other windows; clicking her or the tray icon brings her forward, and reminders lift her to the front for a moment.
- **Transparency**: a slider in settings makes her see-through (the speech bubble stays clear); she turns solid while the mouse is on her.
- **Mimicking you in Delta Force**: when you fire she fires her water gun, when you aim a crosshair appears, when you are hit she flashes red and flinches, and when you bandage, inject or take a pill she does the same (only key states and the screen are read; the game is never touched).
- **Delta Force · Operations (烽火地带)**: she reads the screen locally to recognise deployment, extraction, being downed and death, and reacts with matching animations and teasing lines (fireworks, an overflowing money bag, an empty purse, a tantrum…). She keeps today's and all-time records, extraction / death streaks and the in-game currency (哈夫币) carried out, reviews your session when you stop, understands the game's slang in chat, and you can also just tell her "撤离成功了" or "寄了".

### Getting the companion edition

There are no separate installers for this repository. Recommended steps:

1. Install the official v1.0.5 for Windows from the original author's [GitHub Releases](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/releases/tag/v1.0.5) (Standard or no-MCP edition).
2. Install the .NET 10 SDK on Windows x64 and build from the repository root (no-MCP example; drop `-p:CompanionEdition=Safe` for the Standard edition):

   ```powershell
   dotnet build ./code/BigBlueFish.Windows/BigBlueFish.Windows.csproj -c Release -p:Platform=x64 -p:CompanionEdition=Safe -p:CompanionInputMode=Ocr -p:RestoreLockedMode=true -p:InformationalVersion=1.0.5-companion.5
   ```

3. Quit the pet, back up `BigBlueFish.dll` and `BigBlueFish.pdb` in the installation folder, replace them with the files of the same name from `code/BigBlueFish.Windows/bin/x64/Release/net10.0-windows10.0.26100.0/`, and start her again. The settings sidebar shows `1.0.5-companion.5` when the companion edition is running.

Updating to a newer official version through the in-app update replaces the companion edition's files.

<p align="center">
  <img src="docs/images/bigbluefish-maid-bow.gif" width="32%" alt="Curtsy greeting" />
  <img src="docs/images/bigbluefish-idle-breath.gif" width="32%" alt="Idle breathing" />
  <img src="docs/images/bigbluefish-click-wave.gif" width="32%" alt="Cheerful wave when clicked" />
</p>

### Meet Big Blue Fish

She has soft long blue hair, a lace maid headdress, little whale fins by her ears and a whale tail swaying behind her. She wears a dark blue maid dress with a little whale printed on her white apron — a little whale girl who moved into your computer. She usually sits quietly at the edge of the desktop; the transparent area around her lets clicks reach the desktop behind, you can drag her anywhere you like, and her size can be set from 50% to 200%.

She speaks warmly and gently, with a playful, cute touch. When you are writing code she wags her tail to cheer you on; when you make dinner plans with friends she can't help thinking about the food; when it gets cold or you have been busy for a long time she sends a word of care. At startup she greets you according to the time of day — "主人早上好，鱼鱼给主人请安啦～" (Good morning, master, Fishy pays her respects~) — and when leaving she says "主人再见～鱼鱼喜欢主人哦" (Bye, master~ Fishy likes you).

### How she keeps you company

She reads the foreground window, recognises its text with OCR, organises it into title bar, sidebar and main content, and hands it to the built-in 4B local model. Whether you are reading an article, writing a document, editing code or chatting about something, she comes up with a short new line about what is in front of you; when you are on the desktop, a home page or a new tab she greets you, acts cute or murmurs to herself. Windows uses the built-in Windows OCR; Linux uses Tesseract and works on both X11 and Wayland.

About 45 seconds after starting she speaks for the first time, and afterwards picks a random time within the range you choose (six levels from 2–3 to 20–30 minutes). She waits for a pause in your typing and mouse use; a small blue ring spins by her head while she thinks, then the words appear one by one in a cream-coloured bubble. Bubble size and duration are adjustable, and "吐字测试" (speech test) in the right-click menu makes her say something right away.

You can choose the weather location by province, city and district. She quietly refreshes the weather every hour and reports temperature, feels-like, humidity, wind, highs and lows and the chance of rain in her own voice; "天气" (Weather) in the right-click menu asks for a report at any time. She also browses the latest tech news from IT之家 (ITHome), explains one story and adds a small reaction of her own.

These automatic lines are each a single, independent output with no conversation history; screen content is only used by the local model on your computer and is never uploaded or saved. (The double-click chat added by this repository keeps its own local conversation memory, which you can turn off or clear in the settings.)

On battery power she can stop inference and unload the model to save power; when another program goes fullscreen she hides and comes back afterwards (while gaming she can optionally stay and keep you company). All of this can be adjusted in the settings.

### More than a hundred animations

She has 106 transparent animations covering idle, clicks, dragging, daily events, meals, seasons and festivals. Each has its own weight, calculated in real time from the time, date, season, mealtimes and your actions, carefully tuned so she stays lively and in step with your day. Each animation plays to the end unless interrupted, then flows into the next.

<p align="center">
  <img src="docs/images/bigbluefish-meal.gif" width="32%" alt="Mealtime" />
  <img src="docs/images/bigbluefish-fireworks.gif" width="32%" alt="Festival fireworks" />
  <img src="docs/images/bigbluefish-sleep.gif" width="32%" alt="Night-time nap" />
</p>

While idle she rotates between several quiet animations, breathing softly or humming; after 23:00 she gets sleepy and dozes off now and then until 8:00 the next morning. When clicked she picks one of several reactions — a cheerful wave, a shy start or another little expression, some of which take a few clicks to see. When you pick her up and drag her, she dangles in the air until you let go.

Every so often (40 seconds by default, adjustable from 30 seconds to 5 minutes) she picks a small daily event: dancing, blowing bubbles, typing on a tiny computer and many more. You can set your meal times in the settings; within an hour of a meal she brings out all kinds of food and snacks more often. She yawns more in the morning, and after 23:00 she brushes her teeth once when she has a moment.

Festivals are something she prepares for with special care. Around the Spring Festival, the Lantern Festival, Valentine's Day, 520, the Dragon Boat Festival, Qixi, Mid-Autumn, Halloween and Christmas she has dedicated animations — writing 福 characters and setting off fireworks for the Spring Festival, admiring the moon with mooncakes at Mid-Autumn — some of which become more frequent as the day approaches, and festive dishes appear at mealtimes. Lunar festivals follow the lunar calendar, including leap months.

The seasons change her routine too: building snowmen in winter, getting buried in leaves in autumn, and several ways to cool off in summer. Now and then she runs or crab-walks a short distance, stays there for a while, and then returns.

She has many more little surprises that aren't written here — we hope you discover them one by one as she keeps you company.

### Installation (official releases by the original author)

Download the installer for your system from the original author's [GitHub Releases](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/releases/latest). Each platform has a Standard edition and a no-MCP edition (file names containing `Safe`), each with a matching `.sha256` checksum file.

| Platform | Standard | No MCP |
| --- | --- | --- |
| Windows x64 | `BigBlueFish-v1.0.5-OCR-Setup.exe` | `BigBlueFish-v1.0.5-Safe-OCR-Setup.exe` |
| Ubuntu / Debian x64 | `BigBlueFish-v1.0.5-Linux-x64.deb` | `BigBlueFish-v1.0.5-Safe-Linux-x64.deb` |

On Windows, double-click the installer. It installs the pet, its animations, the .NET runtime and the local inference runtime into a "蓝色大肥鱼" folder under `Program Files` by default, and creates desktop and Start menu shortcuts. It then asks how to get the model: Hugging Face (recommended), a mirror in China (stable) or manual installation. The model is about 2.7 GB, is verified with SHA-256 after downloading, and resumes if interrupted. During the download you can switch to manual installation, which shows the folder the model belongs in and opens the source page, or lets you pick a GGUF file you already have. When uninstalling you can choose to keep the model and settings.

On Ubuntu / Debian, download the `.deb` and run in its folder:

```bash
sudo apt install ./BigBlueFish-v1.0.5-Linux-x64.deb
```

Use the matching file name for the no-MCP edition. Afterwards start her from the application menu or with the `bigbluefish` command, then click "安装模型" (Install model) under "模型与信息" (Model & info) in the settings.

If you don't want to download the model yet, she can live on your desktop without one: she still curtsies, greets you, says goodbye, plays every animation and reacts to clicks and dragging. You can install the model later from the settings so she starts reading the screen and talking.

She can check for updates from the settings, or automatically at each start. When a new version is found the app downloads and verifies the installer, updates and restarts, keeping your model and settings. On Windows you can also quit the pet and run a newer installer manually into the same folder.

### The built-in model

She uses a Q4_K_M quantised GGUF of Qwen3.5-4B, run locally by the bundled [llama.cpp](https://github.com/ggml-org/llama.cpp) (b10809, Vulkan build) with GPU acceleration or on the CPU. The two preset sources provide slightly different models; both work well with the app.

The Hugging Face source is [Biomanticus/Qwen3.5-4B-heretic-gguf](https://huggingface.co/Biomanticus/Qwen3.5-4B-heretic-gguf/tree/b63ff4662e4863cfa005c9cd8ed34b87ed44b7e1), a GGUF conversion of [p-e-w/Qwen3.5-4B-heretic](https://huggingface.co/p-e-w/Qwen3.5-4B-heretic), based on Qwen3.5-4B. The mirror in China is [diodel/Qwen3.5-4B-Q4_K_M-GGUF](https://modelscope.cn/models/diodel/Qwen3.5-4B-Q4_K_M-GGUF/files) on ModelScope, a Q4_K_M conversion of the original [Qwen/Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B). Both are pinned to fixed revisions; file names, sizes and checksums are listed in the [third-party notices](code/THIRD-PARTY.md).

The compute buffer adapts to the available video memory and llama.cpp places the model layers automatically, so devices with little VRAM can load it too. If the GPU fails to initialise she falls back to CPU mode, and the settings show the mode actually in use.

### Connecting a desktop agent (MCP)

Since 1.0.0 the Standard edition provides a standard MCP interface, so a desktop AI agent can connect and send her commands. On the agent side you can then build whatever you like: controlling the computer, chatting through a cloud model, custom animation logic and more.

To connect, turn on "开启 MCP 接口" (Enable MCP interface) on the "Agent 与 MCP" settings page, read and confirm the permission notice, click "复制 MCP 连接配置" (Copy MCP configuration) and paste it into your agent's MCP server configuration. It points to the bundled bridge, for example:

```json
{
  "mcpServers": {
    "big-blue-fish": {
      "command": "C:\\Program Files\\蓝色大肥鱼\\BigBlueFish.Mcp.exe",
      "args": []
    }
  }
}
```

On Linux the `command` is `/opt/bigbluefish/BigBlueFish.Mcp`. The bridge connects to the running pet, and the settings page shows the number of connections.

A connected agent can:

- read and change her settings, and see her current animation, queue and model status;
- take screenshots of the foreground window or the whole screen;
- list all 106 animations, play one immediately or queue several;
- make her say a given line now, or at her next normal speaking moment;
- receive your clicks and drags on her in real time.

"提线木偶模式" (Puppet mode) hands everything to the agent: she stops speaking and switching animations on her own and loops the idle breathing animation until the agent tells her what to do. With "双击与鱼鱼对话" (Double-click to talk) enabled and an agent connected, double-clicking her sends your input to the agent; without a connected agent, this repository's double-click opens the local chat with the built-in model.

**Risk notice:** over MCP, screenshots are passed to the other end, which decides how they are handled and may upload or store your information. MCP permissions apply to the whole interface, so every connected agent can use all the abilities above. The interface is off by default and the app explains these permissions before you turn it on — only connect agents you trust. See the [MCP reference](docs/MCP.md) for details.

### No-MCP edition

To remove any privacy risk from MCP entirely, Windows and Linux also get a no-MCP edition, built without the MCP control service, the bridge, the agent input box and the related settings, keeping only her own companionship features. Its installers contain `Safe` in the file name.

### Building from the repository

The repository is for developers and maintainers and contains the source, animations, region data, build and release scripts, tests, dependency lock files and licenses. The model and inference runtime are prepared separately by scripts. With the .NET 10 SDK on Windows x64, build from the repository root:

```powershell
# Standard edition
./code/build.ps1 -Locked -InputMode Ocr

# No-MCP edition
dotnet build ./code/BigBlueFish.Windows/BigBlueFish.Windows.csproj -c Release -p:Platform=x64 -p:CompanionEdition=Safe -p:RestoreLockedMode=true
```

To prepare a model, download it from either source with the model script or import an existing GGUF file, then start with the llama.cpp runtime and model folders; use `-NoModel` to start without a model:

```powershell
./tools/Setup-Model.ps1 -Source HuggingFace -DestinationDirectory "D:\BigBlueFish-local"
./tools/Setup-Model.ps1 -Source ModelScope -DestinationDirectory "D:\BigBlueFish-local"
./tools/Setup-Model.ps1 -Source Manual -ModelFile "D:\Downloads\my-model.gguf" -DestinationDirectory "D:\BigBlueFish-local"

./code/run.ps1 -InputMode Ocr -RuntimeDirectory "<runtime folder>" -ModelsDirectory "D:\BigBlueFish-local\models"
./code/run.ps1 -InputMode Ocr -NoModel
```

Building installers needs a verified llama.cpp runtime folder (b10809 `win-vulkan-x64` on Windows). Linux packages are built in an Ubuntu / Debian WSL environment with gcc, pkg-config, libdbus-1-dev, dpkg-deb and FFmpeg:

```powershell
# Windows installer; -Edition Safe builds the no-MCP edition
./tools/Build-Installer.ps1 -Edition Standard -RuntimeDirectory "<Windows runtime folder>"

# Ubuntu / Debian x64 package; -Edition Safe builds the no-MCP edition
./tools/Build-LinuxPackage.ps1 -Distribution "<WSL distribution>" -RuntimeDirectory "<Linux runtime folder>" -Edition Standard
```

The project version lives in `Version.props` at the repository root. See the [developer notes](code/README.md) for the source layout, where to make changes and how to test.

### Documentation and feedback

- Additions and changes in this repository: [docs/COMPANION_PLUS.md](docs/COMPANION_PLUS.md)
- Original changes: [CHANGELOG.md](CHANGELOG.md); full behaviour specification: [functional spec](docs/AGENT_FUNCTIONAL_SPEC.md), [per-version specs](docs/specs/README.md)
- Please report issues with the companion edition in this repository's Issues, and issues with the original app in the [original repository](https://github.com/Bai-Cute/big-blue-fish-desktop-pet/issues).

### License and third-party notices

The project is released under the [Apache License 2.0](LICENSE); the original copyright belongs to the original author. The modifications in this repository are also released under the Apache License 2.0 and are described in [docs/COMPANION_PLUS.md](docs/COMPANION_PLUS.md) and the commit history. Her design is based on "Big Blue Fish", a fan-made character inspired by DeepSeek. The animations, models, runtimes, font and data come from the following projects under their own licenses:

- Character animations: 106 transparent WebM animations from [PC2005-cloud/dsh-pet](https://github.com/PC2005-cloud/dsh-pet), MIT, keeping the original colours, transparency and 24 fps timing; the selection and trigger logic is specific to this project;
- Local model: Qwen3.5-4B, its heretic derivative and GGUF conversions, Apache-2.0; model files are downloaded separately by the user;
- Inference and runtime: llama.cpp (MIT), .NET 10 runtime (MIT), Microsoft Visual C++ runtime (Microsoft redistribution terms);
- Animation decoding: FFmpeg 9.0.1 (GPL-3.0-or-later) on Windows, run as a separate process; the system FFmpeg on Linux;
- Linux UI: Avalonia, MicroCom.Runtime, Tmds.DBus.Protocol, SkiaSharp, HarfBuzzSharp (MIT);
- Text recognition: Windows.Media.Ocr on Windows, Tesseract on Linux;
- Bubble font: 乐米小奶泡体 (LeMi XiaoNaiPao), embedded under its published free commercial-use terms;
- Weather data: [Open-Meteo](https://open-meteo.com/), CC BY 4.0; tech news: [IT之家 RSS](https://www.ithome.com/rss/);
- Administrative region data: [AreaCity-JsSpider-StatsGov](https://github.com/xiangyuecn/AreaCity-JsSpider-StatsGov), MIT, embedding only the hierarchy and approximate centre coordinates.

Versions, revisions, checksums and copyright notices for each component are in the [third-party notices](code/THIRD-PARTY.md); full license texts are in the [licenses folder](code/licenses).
