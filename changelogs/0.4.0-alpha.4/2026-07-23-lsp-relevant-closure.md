# IDE/LSP 精确依赖闭包与性能门禁

- LSP snapshot 不再因任意打开文档或工作区目录变化而全局失效；只沿已解析 import 的反向依赖闭包清理受影响文档。
- 打开的 imported source 现在作为 unsaved overlay 参与语义编译，关闭后精确回退到磁盘内容。
- snapshot dependency stamp 只读取当前项目配置、构建输入与传递 import 闭包，稳定热请求不再枚举 source/import/package roots。
- 新增 cold/hot/edit 长会话性能门禁，记录 snapshot compile/cache hit、目录扫描数、dependency fingerprint 时间及 P50/P95/P99 延迟；CI 要求无关文档不触发重编译、热路径目录扫描为零。
