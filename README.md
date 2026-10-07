# Luma Translate 1.1.1

原生 Windows / macOS 即时英译中工具，采用雾白与鼠尾草绿界面。macOS 使用原生毛玻璃材质；Windows 使用清晰的浅色面板和圆角。保留原有蓝紫色双页、声波与点击星芒 Logo；Windows 主界面改用独立 512 px 图片，修复放大托盘小图标导致的模糊。品牌原图与图案保持不变，安装包包含 Windows 多尺寸 ICO 和 macOS ICNS。

## 安装与使用

- Windows 10 2004+ / Windows 11 x64：运行 `Luma-Translate-Windows-1.1.1-Setup.exe`。需要 .NET Framework 4.8 和系统英文 OCR 组件。
- macOS 13+：打开通用版 DMG，将应用拖入 Applications。支持 Apple Silicon（包括 M1）与 Intel。
- 右键双击英文进行离线取词；输入或粘贴单词、短语、谚语也可查询。
- 按住右键直接拖拽可框选词语、短语与句子，不需要等候长按，也不需要配置密钥。本地未收录的整句仅提供明确标注的逐词参考。
- 配置 DeepSeek / Gemini 并同意发送英文后，可使用 AI 详解和框选整句翻译。短语块返回详细解释；超过 30 个英文词的框选内容使用整句翻译。
- 释义浮窗使用较大字号、独立滚动区与“展开阅读”入口；Mac 浮窗可拖动、调整大小，点击内部不会关闭。
- Mac 首次屏幕取词需要辅助功能与屏幕录制权限。详见 `INSTALL.txt`。

本构建未使用商业 Windows 代码签名证书，macOS 使用 ad-hoc 签名且未经过 Apple 公证。首次打开可能需要通过系统对本应用的来源确认。

## 词典与学术英语

包含 767,778 条基础记录，其中 365,230 条含多个词；158,731 条有英文释义，217,915 条有词库音标。另有 34 条学术术语与语块补充，附原创例句和用法说明。计数来自固定版本的 ECDICT 数据，不表示所有条目都包含完整字段或所有义项。

- 完整保留词库已收录的释义；长内容可滚动查看。
- 缺少音标、英文解释或例句时明确提示，不用推测内容填补空白。
- AI 详解按义项编号组织中文、英文解释及双语例句，要求英美音标、学术搭配和覆盖说明。点击词与 OCR 所在行作为不同字段传入，避免把上下文替代查询词。
- AI 内容和例句标为生成内容，不能承诺穷尽所有历史、罕见或专业义项。
- 未匹配的语块只提供明确标注的逐词参考，不冒充整句翻译。

词库来源、固定提交、散列与补充说明见 `source/data/dictionary-manifest.json`。ECDICT 保留 MIT 许可证。补充例句是语言示例，不是研究结果或文献引文。

## 构建

Windows（PowerShell）：

```powershell
./source/build.ps1 -Test -VisualTest
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' installer/windows.iss
```

macOS（Xcode Command Line Tools / Swift 5.9+）：

```sh
swift test --package-path macos
bash macos/scripts/build-universal.sh
bash macos/scripts/verify-runtime.sh
```

词库重建：先下载 manifest 中固定 ECDICT 提交的 `ecdict.csv`，然后运行：

```sh
python source/build_dictionary.py /path/to/ecdict.csv --source-commit COMMIT_SHA
```

Windows 核心/界面测试覆盖手势状态转换、系统 OCR 引擎与真实控件，并检查释义末尾能通过滚动到达。macOS 从 DMG 复制安装后，在 Apple Silicon 与 Intel 运行相同通用包：对另一应用的测试文档发送实际全局右键双击、直接右键拖拽和滚轮事件，验证截图、Vision OCR、离线查询、浮窗、内部点击及滚动到末尾。Windows 全局右键手势仍需在目标设备实测；构建测试也不保证第三方 API 的实际语义质量。用户设备需自行授予系统权限。

通过 `.github/workflows/verify-mist-ui.yml` 在新分支构建安装包；主分支和旧发行版保留。
