OPC UA ⇌ S7-1511 全双工工业智能网关 (OPCToS7 Gateway)

[![Platform](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![UI Framework](https://img.shields.io/badge/UI-HandyControl-purple.svg)](https://github.com/HandyOrg/HandyControl)
[![PLC Lib](https://img.shields.io/badge/PLC-Sharp7-orange.svg)](https://sourceforge.net/projects/snap7/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

OPCToS7 Gateway 是一款为 探险家Mr胡佳 设计的高性能、全双工、双向防抖工业网关系统。本系统基于最新 **.NET 10** 框架开发，向下无缝兼容西门子 **S7-15XX PLC (基于 Sharp7 驱动)** 核心协议，向上无缝对接 **OPC Foundation 标准客户端/服务器**。

系统独创了**“双重缓存（Twin  Cache）+ 异步双核调度”**架构，解决了异构工业通讯中“正反向数据回音（Echo Loop）”导致的无限死循环抽搐问题。界面层全面整合 **HandyControl** 开源高端流体 UI 库，兼顾科技感的工业审美与高频刷新的极致性能。

---

## 📌 核心技术特性

### 1. 全双工高频双向同步引擎
系统彻底打破传统网关单向传输或伪双向的限制，同时开启两条高并发流水线：
* **正向通道 (OPC UA → S7 PLC)**：基于事件驱动（Event-Driven）的 MonitoredItem 变化监听机制，毫秒级捕获外部指令并推入高效大端字节流写入队列。
* **反向通道 (S7 PLC → OPC UA)**：基于高性能自适应时间微拍节（10ms~100ms）轮询机制，主动“吸取”PLC 现场状态变化并强力反冲同步给 OPC 服务器。

### 2. 防回音/死循环防火墙 (Shadow Cache)
针对定时轮询引发的“异步覆盖竞态条件（Async Overwrite Race）”，系统引入了顶级 SCADA 软件防抖策略：
* **冷却死区锁 (Cooldown Timer)**：当 OPC 成功写入 PLC 后，自动对该点位施加 500ms 的物理读取冷却锁定，期间彻底屏蔽 PLC 读取的旧值回音。
* **脏数据对比 (Compare Before Write)**：在内存中常驻 `ConcurrentDictionary` 双向寄存器。当数据无实质性改变，或属于自身写入造成的物理震荡时，在网关内核层面直接执行 0 损耗拦截，节约网络带宽。
* **模拟量底噪死区过滤**：针对 `REAL` 浮点型点位，提供微小震荡免疫阈值（Epsilon = 0.001），有效过滤传感器物理底噪跳动，避免无效网络 IO。

### 3. 性能优化 (Zero-Allocation 零内存分配)
专为高频定时器（10ms 级别）设计的内存清洗管理：
* **IO 合并打包机制**：网关在启动时将所有点位按西门子 **DB 块** 实施常驻内存的分组（GroupBy）预编译缓存。100 个同 DB 变量在轮询时会被自动合并为 **1 条** 物理 DBRead 报文，在 C# 内存中执行极速切片与大端解包，极大保护了 PLC 的背板通讯堆栈。
* **0 GC 压力设计**：彻底移除了运行时循环体内的任何 LINQ 语句、动态解析和 `new` 临时对象。自适应生成全局唯一的 `byte[]` 缓冲区，最大程度压榨 .NET 10 的垃圾回收吞吐性能，杜绝通讯“掉帧”。
* **等宽 UI 表格**：数据监控列强制采用 `Consolas` 等宽字体与动态 `StatusColorConverter` 颜色路由。在数据高频闪烁时，单元格绝不发生长宽抽搐变形。

### 4. 西门子原厂标准协议兼容
* **STRING 封箱/拆箱完全对齐**：完美兼容博途（TIA Portal）标准的 `STRING` 规范。写入时自动在第 0 字节注入最大长度（254），第 1 字节注入实际有效字符长度，第 2 字节开始执行 ASCII 码高密度无缝拷贝。

---

## 🏗️ 系统架构图

系统的核心调度结构由 `TransferEngine` 中枢、`OpcUaClientManager` 通讯类以及 `S7PackManager` 封包机协同运转：
