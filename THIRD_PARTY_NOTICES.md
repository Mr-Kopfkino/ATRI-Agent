# 组件来源与许可范围

本文说明本源码快照的组件来源与许可范围，不构成完整运行环境的依赖清单，也不授予未收录资源的使用权。代码快照日期：2026 年 10 月 23 日；人格与知识资料及本文更新日期：2026 年 10 月 25 日。既有文档横幅维持 2026 年 10 月 22 日版本。

## 1. 代码许可与初期参考

本项目在设计初期曾参考 [Amadeus](https://github.com/Code-Amadeus/Amadeus) 的部分设计，随后围绕角色对话、记忆、语音、桌面交互与工作流程持续迭代。当前系统架构与功能以本项目的实现说明为准。

根目录与 `agent-runtime/` 保留 AGPL-3.0 完整许可文本。源码包仍沿用上述参考项目的少量底层组件，涵盖任务账本、类型合同、Pi JSONL 传输、产物登记、Git/Diff、AUIP 合同及剧本索引；以下文件范围用于记录代码来源与归属，未核定对应的上游 Git 提交。

实际保留的 15 个原模块：

- `agent_host/__init__.py`
- `agent_host/browser_request_contract.py`
- `agent_host/pi_rpc.py`
- `agent_host/provider_contract.py`
- `agent_host/provider_outcome.py`
- `agent_host/provider_types.py`
- `agent_host/work_ledger_store.py`
- `agent_host/work_ledger_types.py`
- `server/__init__.py`
- `server/auip_contract.py`
- `server/local_git.py`
- `server/unified_diff.py`
- `server/work_artifact_registry.py`
- `vn_player/script_index.py`
- `vn_player/text.py`

`runtime/capabilities/node/policy.mjs` 的工作区路径策略适配自同一来源，保留 AGPL 归属；具体来源路径仍记录于该文件的原有注释。`vn_player/__init__.py` 为本快照新增的命名空间标记，用于脚本索引导入。相关源文件原有的版权、归属及参考注释均已保留。

代码快照中的聊天 WORK 协议、轮次生命周期、简记入口、WPF 调度及隔离合同检查复用上述账本与权限实现，不改变既有组件许可证。公开副本的 `ContextBuilder.Rules` 保持通用化，并保留可选 `work` 请求合同。2026 年 10 月 25 日另加入原项目实际使用的 `assets/derived/persona.json` 与 `assets/derived/knowledge.json`；其资料范围与来源说明见第 6 节，不改变上述代码许可证。

## 2. GPT-SoVITS 相关调用与提取逻辑

`runtime/voice/engine.py`、`scripts/prepare-voice.py` 等涉及 [RVC-Boss/GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS) 的推理接口与源码提取。上游 MIT 许可文本保留为 `runtime/voice/LICENSE.GPT-SoVITS`，其中版权行为 `Copyright (c) 2024 RVC-Boss`。另保留 `runtime/voice/LICENSE.APACHE-2.0` 供相关模块来源约束查阅。

本快照未收录 GPT-SoVITS vendor 代码、角色权重、HuBERT、ERes2Net、参考音频、词典及 Python 推理环境。模型与资源适用各自条款；后续引入第三方实现时，应保留准确版本、嵌套许可及原有声明。

## 3. Pi、Playwright 与 Node.js

`runtime/capabilities/node/package.json` 和 `package-lock.json` 是依赖声明，实际包代码、Node.js 与 Chromium 未包含。Pi 完整 MIT 文本保留在 `licenses/pi-MIT.txt`，版权行为 `Copyright (c) 2025 Mario Zechner`。对应上游为 [earendil-works/pi](https://github.com/earendil-works/pi)。

Playwright Core、TypeBox、`pi-simple-web-tools` 及间接依赖适用各自许可，Pi 或本项目许可不覆盖全部 npm 依赖。包锁中的来源地址与完整性字段用于固定依赖，不包含用户认证配置。

## 4. ASR、VAD、E5 与仲裁模型

仓库保留工作进程、公开下载地址、版本／哈希约束和接口代码，没有提供 sherpa-onnx、ONNX Runtime、SenseVoice、Silero VAD、multilingual-e5-small 或 Whisper 的实现安装包与模型文件。

这些组件的代码许可与模型许可可能分别定义，重新部署时应分别核对。相关官方入口：

- [sherpa-onnx SenseVoice 部署说明](https://k2-fsa.github.io/sherpa/onnx/sense-voice/index.html)
- [Silero VAD](https://github.com/snakers4/silero-vad)
- [multilingual-e5-small 固定模型卡](https://huggingface.co/intfloat/multilingual-e5-small/blob/614241f622f53c4eeff9890bdc4f31cfecc418b3/README.md)

## 5. PSB 结构解析

`scripts/psb.py` 是项目只读结构解析器，读取未加密 PSB v2/v3。格式参考来源为 [FreeMote.Psb/Psb.cs](https://github.com/UlyssesWu/FreeMote/blob/master/FreeMote.Psb/Psb.cs)；原文件的参考说明已保留。此仓库不附带 FreeMote 二进制、原作 PSB 容器或游戏剧本。

PBD／TLG 参考代码、GARbro 参考程序及资源解包工具未纳入本快照。

## 6. 原作角色、商标、音频与图像

项目代码中的 ATRI 等名称及资源路径用于标识原工程，不构成相应作品、角色、声线或商标的开源授权。仓库未提供完整原作剧本、应用运行美术与音频或人物模型。

`assets/derived/persona.json` 与 `assets/derived/knowledge.json` 从原项目按字节复制，包含项目实际采用的完整人格配置及游戏早期共同日常时期的知识条目、出处和少量短句示例；它们不是完整剧本或全剧情资料库。两份文件保留原有字段、相对来源路径与公开链接，没有加入用户会话、私有记忆或账户配置。`assets/ATRIplay/...`、`docs/persona-audit.md` 等相对路径仅用于定位原工程的来源记录，对应原作剧本与内部审计文档不随包提供。

两份 JSON 中的原作角色、剧情设定及引用台词的权利仍归相应权利人所有，不因根代码的 AGPL-3.0 许可取得额外授权。随文件提供的项目编写规则、来源注释和原作相关内容需按各自来源区分；再次使用、修改或分发时应分别核对适用条款。

`docs/readme/atri-agent-readme-banner.png` 使用项目已有角色立绘与背景作为参考，经 ImageGen 生成，仅用于文档展示。原作角色与源美术的权利归相应权利人所有，根代码的 AGPL-3.0 许可不授予这些材料的额外使用权。来源见 [文档资产说明](docs/readme/ASSETS.md)。

训练语料、人物声音及游戏资源需取得独立适用授权。后续贡献应使用独立创作或具有适用授权的内容，并保留来源与许可。
