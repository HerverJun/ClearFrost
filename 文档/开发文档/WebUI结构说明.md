# Web UI 结构说明

ClearFrost 的前端运行在 WinForms 内嵌 WebView2 中，当前采用静态资源方式组织，不依赖 Node.js 构建链。

## 目录边界

- `ClearFrost/html/index.html`：WebView2 加载入口，承载主页面结构。
- `ClearFrost/html/css/style.css`：业务界面样式。
- `ClearFrost/html/js/*.js`：前端源码模块，日常维护应修改这些文件。
- `ClearFrost/html/js/bundle.js`：MSBuild 在构建前按 `ClearFrost.csproj` 中的顺序拼接生成，用于运行时加载。
- `ClearFrost/html/cropper.min.*`、`tailwind.min.js`：本地化第三方前端依赖。

## 维护约定

1. 不直接修改 `bundle.js`。需要改前端逻辑时，修改对应的 `html/js/*.js` 源文件。
2. 新增前端模块后，需要同步更新 `ClearFrost.csproj` 中 `BundleWebUiScripts` 的拼接顺序。
3. WebView2 和 C# 交互逻辑优先放在 `bridge.js` 及 `Views/WebUIController*.cs` 相关文件中。
4. 若未来引入 Node.js/Vite 等构建链，应先将 `html` 升级为独立前端子模块，并保留发布到 `ClearFrost/html` 的静态输出约定。
