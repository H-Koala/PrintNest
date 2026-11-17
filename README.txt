印巢 PrintNest 1.0.1
=================
Windows 10 2004（19041）或更新版本 / Windows 11，64 位。

1.0.1：修复文件列表复选框被裁切的问题，为选择框预留独立列并移除主题默认留白。

使用
----
双击 PrintNest.exe。成品已包含 .NET 运行环境，不需要 Python、WPS 或另装 .NET。
保持 licenses 文件夹与程序一起分发。首次运行会在 Windows 临时目录解压随附的本机库。

1. 添加文件、添加文件夹（包括子目录），或者拖入 PDF / 图片。
2. 文件默认按名称自然排序；用 ↑ ↓ 调整队列顺序，勾选参与本次打印的文件。
3. 在右侧选择打印机、纸张、横竖方向、每张纸的页数、缩放、颜色、双面和边距。
4. 选中文件后，在右侧下方单独设置份数、页码（如 1-3,5）或旋转角度。
5. 查看中间的纸张预览，点击“导出为 PDF”或“开始打印”。真正打印前会显示数量确认。

快捷键：Ctrl+O 添加文件，Ctrl+P 打印，Delete 从队列移除文件（不会删除源文件）。

功能
----
- PDF、JPG/JPEG、PNG、BMP、TIFF/TIF、GIF 混合批量导入。
- 多页 PDF 和 TIFF；GIF 只打印首帧。
- 1 / 2 / 4 / 6 / 9 合 1 拼版，PDF 页面和图片可连续混排。
- 完整显示、等比铺满裁切、拉伸；手动旋转；JPEG EXIF 方向处理。
- A4、A5、Letter、4×6 英寸照片纸，纵向 / 横向。
- 150 / 300 / 600 DPI；灰度预览与输出；打印机支持时可自动双面。
- 自然排序、去重、缩略图、分页预览、队列保存 / 恢复。
- 保存常用设置到 %LOCALAPPDATA%\PrintNest\settings.json。
- 打印准备阶段将页面缓存到临时目录，按页提交到同一个 Windows 打印任务。
- 可以停止准备 / 导出；已经进入 Windows 队列的页需在 Windows 打印队列中取消。

份数与拼版规则
--------------
先按列表顺序展开每个文件的选定页码与单文件份数，再连续拼版。
例如 A 文件 2 份，B 文件 1 份，队列为 A、A、B，然后按所选“每张纸页数”分组。
整批份数复制整套输出。预览只展示一整套；底部显示整套页数 × 整批份数。
拼版可以跨文件边界；双面时相邻文件也可能位于同一张纸的正反面。

边界说明
--------
打印和 PDF 导出采用所选 DPI 栅格化；导出的 PDF 不保留可选择文本、链接或表单。
加密 PDF 需要先自行解锁。暂不直接支持 Word、Excel、HEIC、WebP、SVG、XPS。
纸张、双面、无边距能力取决于打印机驱动；不支持的纸张 / 双面设置会给出错误。
打印机硬件不可打印区域可能使内容区比预览更小；建议保留 8 mm 边距。
“打印机属性”打开 Windows 系统打印对话框；其中“打印”仅应用属性，程序此时不提交文档。
纸张、颜色、方向、份数、双面以主窗口右侧设置为准；纸盒等其他属性保留驱动设置。
队列文件只保存绝对路径和设置，不内嵌源文件；移动源文件后需要重新添加。
程序只在本机处理文件，没有账号、订阅、上传、遥测或自动更新。
本版本未做数字签名。物理打印机兼容性需要在你的设备上验证。

开发与构建
----------
技术：C# / .NET 10 / WPF；WPF UI 4.3.0；Windows.Data.Pdf；PDFsharp 6.2.4。
源码采用 MIT 许可证。第三方组件许可证见 licenses；UI 参考 https://github.com/lepoco/wpfui。
从源码构建需要 .NET 10 SDK（仅开发者需要），运行成品不需要 SDK。

在源码目录执行：
dotnet restore PrintNest.csproj
dotnet publish PrintNest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist

运行功能自检（生成的都是示例文件，不会提交物理打印任务）：
PrintNest.exe --self-test C:\path\to\empty-test-folder

核心文件：
MainWindow.xaml / MainWindow.xaml.cs  界面、导入、设置和任务协调
DocumentService.cs                   PDF / 图片解码及共用排版
OutputService.cs                     PDF 导出、打印与分页缓存
GdiPrinter.cs                        原生 Windows GDI 打印
Models.cs                            队列模型、严格页码解析、设置保存
SelfTest.cs                          排版、导出、取消及原生 GDI 图像传输测试
