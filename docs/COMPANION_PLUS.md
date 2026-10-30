# MyBigBlueFish 修改说明

本仓库基于 [Bai-Cute/big-blue-fish-desktop-pet](https://github.com/Bai-Cute/big-blue-fish-desktop-pet) 的 v1.0.5（Apache License 2.0）修改。原作者的代码、动画素材（来自 [PC2005-cloud/dsh-pet](https://github.com/PC2005-cloud/dsh-pet)，MIT）、许可文件和第三方声明均保持不变；下面列出本仓库新增和修改的内容。

修改后的程序在设置页左侧显示版本号 `1.0.5-companion`（构建时传入 `-p:InformationalVersion=1.0.5-companion`）。所有新增数据都只保存在本机安装目录的 `data\` 中，不上传。

## 一、沿屏幕四周爬行

- 鱼鱼用「螃蟹走路」动作沿当前显示器的可用区域（不含任务栏）四周横着爬：底边站立，左右两侧贴墙（旋转 ±90°），顶边倒挂（180°），拐角处顺势转身且始终贴着两条边。
- 设置 → 陪伴与外观 →「沿屏幕四周爬行」：关闭 / 偶尔（15%）/ 经常（40%）/ 一直爬（巡游，默认）；「爬行速度」：慢 / 中 / 快。
- 巡游中到了动作触发时间、又正好爬在底边时，她会停下来做吃饭、跳舞、节日等动作，然后接着爬。
- 点击：停下回应后继续；拖动：立即停止，放下后歇一会儿再从最近的边缘继续。
- 右键：「开始巡游 / 停止巡游」（其他模式为「沿屏幕爬一圈 / 停止爬行」）；停止时她会快步爬到底边再停下。
- 聊天、专注、玩游戏时自动停止爬行。

## 二、互动

- **双击聊天**：双击鱼鱼弹出输入框（Enter 发送，Shift+Enter 换行，Esc 收起），内置本地模型以鱼鱼的口吻多轮回答，回答显示在气泡中。
- **会淡忘的记忆**（`data\memory.json`）：
  - 每段对话按重要程度记忆：名字、生日等最重要，喜好、计划、心情次之，闲聊最容易忘。
  - 记忆强度随时间按遗忘曲线衰减；被想起一次就会记得更久。
  - 清楚的记忆可以自然提起；模糊的记忆会丢失数字和细节、时间可能记错；只剩印象的只记得大概话题；彻底淡忘的会被清理。
  - 聊天时根据主人说的话回忆相关片段，想不起来会老实说「记不太清了」。
  - 设置里可关闭记忆或一键清空。
- **称呼**：说「叫我小陈」「我叫××」后，她会用这个称呼；「叫我主人」可改回。
- **摸头**：鼠标在她头上左右来回轻划几下（不按键）。**戳戳**：单击有反应；短时间连戳会傲娇生气。
- **喂零食**：右键「喂零食…」（大白饭、小鱼干、冰淇淋、糖葫芦、西瓜、饺子），每天最多 6 次。
- **好感度与心情**（`data\bond.json`）：摸头、喂食、聊天、专注、陪伴时长都会增加好感（每天有上限），乱戳会减少；好感度分为初识 / 熟悉 / 亲近 / 亲密 / 心有灵犀，影响她聊天时的亲密程度。心情会随时间、饭点和互动变化。很多天没见时会说想你。
- **离开回来**：离开电脑 10 分钟以上再回来时挥手迎接。

## 三、关怀

- **久坐 / 喝水 / 护眼提醒**：按实际使用电脑的时间计算（默认 50 / 60 / 45 分钟，可调）；离开 5 分钟以上算作休息。
- **早安 / 催睡 / 晚安**：每天第一次用电脑（5–11 点）道早安；23:30 以后还在用电脑时每 30 分钟催睡（最多 4 次）；晚上关闭时说晚安。
- **番茄钟**：右键「专注 N 分钟」（默认 25 分钟、休息 5 分钟），专注时安静陪伴，不爬、不说话，提醒延后；聊天里说「开始专注」「结束专注」也可以。
- **定时提醒**（`data\reminders.json`）：右键「提醒事项…」或在聊天里说「30分钟后提醒我开会」「下午3点半提醒我交报告」「明天早上8:30叫我起床」。到点跳出提醒；重启后保留，错过的会补提醒。
- **游戏陪玩**：识别前台的常见游戏（Steam、Epic、WeGame、米哈游、暴雪、Riot 等，可在设置中补充进程名）：
  - 全屏不隐藏，鼠标点击穿过鱼鱼本体；
  - 游戏中不调用模型并释放模型占用的显存；
  - 开局加油、约每 20 分钟加油、每满 1 小时提醒休息、结束后关心战况；
  - 可选（实验，默认关闭）：每 15 秒本机 OCR 识别「胜利 / 失败」字样并做出反应。

所有新设置集中在 设置 → **互动与关怀**。

## 四、构建

与上游相同，需要 Windows x64 和 .NET 10 SDK。无MCP版：

```powershell
dotnet build ./code/BigBlueFish.Windows/BigBlueFish.Windows.csproj -c Release -p:Platform=x64 -p:CompanionEdition=Safe -p:CompanionInputMode=Ocr -p:RestoreLockedMode=true -p:InformationalVersion=1.0.5-companion
```

把 `code/BigBlueFish.Windows/bin/x64/Release/net10.0-windows10.0.26100.0/` 中的 `BigBlueFish.dll`、`BigBlueFish.pdb` 替换到已安装的官方 v1.0.5 程序目录即可（请先备份原文件）。

## 五、改动的文件

新增（`code/BigBlueFish.Windows/Companion/`）：

| 文件 | 内容 |
|---|---|
| `CompanionCrawl.cs` | 沿屏幕爬行：路径、旋转、巡游 / 单圈 / 停止、拐角贴边 |
| `CompanionChat.cs` | 双击聊天窗口、对话上下文、称呼识别、聊天内的提醒与专注指令 |
| `CompanionBrainChat.cs` | 本地模型多轮对话接口（`CompanionBrain` 的 partial） |
| `CompanionMemory.cs` | 会淡忘的长期记忆（遗忘曲线、模糊化回忆） |
| `CompanionBond.cs` | 好感度、心情、每日统计、称呼 |
| `CompanionCare.cs` | 关怀计时、离开回来、早安催睡、健康提醒、番茄钟、摸头、戳戳、喂零食 |
| `CompanionReminders.cs` | 定时提醒（中文时间解析、持久化、提醒面板） |
| `CompanionGame.cs` | 游戏识别与陪玩 |
| `CompanionCareSettings.cs` | 设置页「互动与关怀」 |

修改：`CompanionAnimations.cs`、`CompanionWindow.cs`、`CompanionSettings.cs`、`CompanionMenus.cs`、`CompanionPreferences.cs`、`CompanionMcp.cs`、`CompanionBrain.cs`（改为 partial）；以及若干测试夹具（关闭爬行、更新菜单预期，保证原有自动化测试行为不变）。
