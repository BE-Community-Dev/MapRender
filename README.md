# Bedrock Level —— C# 存档解析（纯 IO 自研实现）

把原 C++ 项目 `bedrock-level` 的“解析存档”部分用 C# 重写。解析层**不依赖任何第三方 NuGet 库**，全部从字节流层面手写实现；区块解析完成后用 .NET 内置的 **Brotli（质量 11，最高压缩率）** 压缩并存入**内存**或**磁盘缓存文件**，避免常驻大量未压缩数据。

---

## 1. 环境要求

- .NET SDK 8.0 或更高（构建本仓库使用的是 .NET 10 SDK，目标框架为 `net8.0`）
- 操作系统：Windows / Linux / macOS 均可（纯托管代码）

## 2. 目录结构

```
csharp/
├── BedrockLevel.sln
└── src/
    ├── BedrockLevel/            # 类库（可复用）
    │   ├── IO/                  # ByteReader / ByteWriter（小端）
    │   ├── Nbt/                 # NBT 解析器（小端，完整 Tag 类型）
    │   ├── LevelDb/             # 自研 LevelDB 读取器
    │   │   ├── Varint.cs        # 变长整数
    │   │   ├── ByteString.cs    # 字典键（字节串）
    │   │   ├── BlockHandle.cs   # Block 句柄
    │   │   ├── Block.cs         # Block 解析（前缀压缩）
    │   │   ├── ZlibDecompressor.cs  # zlib / raw-deflate 解压（内置）
    │   │   ├── SstFile.cs       # SSTable 读取
    │   │   ├── LogReader.cs     # WAL / MANIFEST 记录帧 + WriteBatch
    │   │   ├── Manifest.cs      # VersionEdit 解析
    │   │   └── LevelDbStore.cs  # 合并所有 entry
    │   ├── Keys/                # chunk_key / actor_key / digest / village 解析
    │   ├── Chunk/               # SubChunk / Biome3D / RawChunk / Chunk / Actor
    │   ├── Cache/               # ChunkCache（Brotli 内存 + 文件）
    │   └── Level/               # LevelDat / BedrockLevel 顶层入口
    ├── BedrockRender/           # 渲染库（生成区块剖面图）
    │   ├── Resources/           # biome_color.json / block_color.json（嵌入资源）
    │   ├── ColorPalette.cs      # 生物群系 / 方块调色板 + 生物群系着色
    │   ├── ChunkRenderer.cs     # 区块 → 像素图（Surface / Biome / Height）
    │   └── BedrockRender.csproj
    └── BedrockRender.Demo/      # Avalonia 示例（读全部区块并拼接到画布 / 导出 PNG）
```

## 3. 编译

在项目根目录下执行：

```bash
cd csharp
dotnet build -c Release
```

仅编译类库：

```bash
dotnet build src/BedrockLevel/BedrockLevel.csproj -c Release
```

## 4. 运行控制台示例

### 4.1 解析一个真实存档

把一个 Minecraft 基岩版存档目录（里面应有 `level.dat` 和 `db/` 子目录）传给程序：

```bash
dotnet run --project src/BedrockLevel.Demo/BedrockLevel.Demo.csproj -- <存档目录> [缓存目录]
```

示例输出：

```
Opening save: D:\Saves\MyWorld
  Level name : MyWorld
  Spawn      : (12, 64, -8)
  DB keys    : 12345
  Chunks     : 1024

Cached 1024 raw chunks in 123 ms.
  Cache dir        : C:\Users\...\bedrock-chunk-cache
  Raw bytes        : 12,345,678
  Compressed bytes : 1,234,567
  Compression ratio: 10.00% (lower = smaller)
  Process memory   : 45,678,912 bytes

Sample chunk [0, 0, 0]:
  version        : New
  entities       : 3
  block entities : 2
  top biome      : 1
  surface block  : minecraft:grass
```

### 4.2 自检测试（无需真实存档）

程序内置 `--selftest`，会**现场合成**一个合法的 LevelDB SST 文件 + `level.dat`，然后跑完整“解析 → 压缩缓存 → 回读”链路，验证自研读取器、NBT、区块解析与缓存往返是否正确：

```bash
dotnet run --project src/BedrockLevel.Demo/BedrockLevel.Demo.csproj -- --selftest
```

输出 `[selftest] PASS` 即通过。

## 5. 作为类库使用

引用 `BedrockLevel` 项目后，典型流程如下：

```csharp
using BedrockLevel.Keys;
using BedrockLevel.Level;
using BedrockLevel.Cache;
using BedrockLevel.Chunk;

// 1) 打开存档（读取 level.dat + db/）
var level = new BedrockLevel();
if (!level.Open(@"D:\Saves\MyWorld"))
    return;

// 2) 元数据
string name = level.Dat.LevelName;
var spawn = level.Dat.Spawn;

// 3) 枚举所有区块坐标
foreach (var cp in level.ChunkPositions())
{
    // 4) 读取原始区块（未解析的键值集合）
    var raw = level.GetRawChunk(cp);

    // 5) 完整解析为结构化区块
    var chunk = level.GetChunk(cp);
    if (chunk == null) continue;

    string topBlock = chunk.GetBlockName(0, chunk.GetHeight(0, 0), 0);
    byte topBiome  = chunk.GetTopBiome(0, 0);
}

// 6) 压缩缓存：内存 + 磁盘文件（Brotli 质量 11）
var cache = new ChunkCache(@"D:\cache");   // 不传路径则仅内存
foreach (var cp in level.ChunkPositions())
{
    var raw = level.GetRawChunk(cp);
    if (raw.Loaded())
        cache.Store(cp, raw);             // 自动压缩后写入
}

// 回读（从压缩缓存还原 RawChunk）
var restored = cache.Load(new ChunkPos(0, 0, 0));
double ratio = cache.CompressionRatio;    // 压缩后 / 压缩前
```

## 6. 压缩与缓存说明

- 压缩算法：`.NET` 内置 `System.IO.Compression.BrotliStream`，`CompressionLevel.SmallestSize` 对应质量 11（最高压缩率），**不涉及任何 NuGet 依赖**。
- 缓存后端（`ChunkCache`）：
  - **内存**：`Dictionary<(x,z,dim), byte[]>` 存放压缩后的字节，原 `RawChunk` 不常驻，回读时再解压。
  - **文件**：每个区块写成 `<x>.<z>.<dim>.blkcache`（`Brotli` 压缩流），几乎不占内存。
- 缓存内容是 `RawChunk.ToRaw()` 的自定义二进制格式（魔数 `BCHK` + 普通键 + 子区块 + 实体摘要 + 实体），因此可无损还原并经 `Chunk.LoadFromRawChunk` 重新解析。

## 7. 实现要点与约束

- **LevelDB 读取范围**：完整只读实现 —— 读取 `CURRENT` 定位 `MANIFEST`，解析 `MANIFEST` 得到存活 SST 集合与 `last_sequence`；读取所有 `*.sst` / `*.ldb` 数据文件；读取除 `MANIFEST` 外的所有 `*.log`（WAL）回放 `WriteBatch`。按 `(user key, sequence, type)` 合并，删除标记（`type=0`）隐藏对应键。
- **Block 解压**：Block 尾部 1 字节为压缩 id：`0`=无、`1`=Snappy（`Snappy.cs` 自研解压，Google Snappy block 格式）、`2`=zlib（RFC1950）、`4`=raw-deflate（RFC1951）。Snappy 为 Minecraft 基岩版 LevelDB 的默认压缩，已完整实现，无需任何 NuGet 依赖。
- **NBT**：基岩版 NBT 为**小端**，已与 `palette.cpp` 对齐。
- **均匀方块层**：`bits==0` 的 uniform 层直接跟单个调色板 Compound，**没有** palette 长度前缀（与 `sub_chunk.cpp` 行为一致）。

## 8. 已知限制

- 没有真实基岩版存档样本的情况下，SST / MANIFEST / WAL / zlib block 的真实路径已按规范实现并通过合成数据自测，但**未经真实存档端到端验证**。若提供真实存档目录，可据此校准压缩 id 映射或任何字节偏移。
- 归档压缩支持 bedrock LevelDB 使用的 None / Snappy / zlib / raw-deflate；若遇到其它压缩类型（如 zstd），该 block 会回退为原样并被判为无效，跳过该块而非崩溃。
- `Chunk` 目前解析了地形（子区块）、生物群系/高度图、实体、方块实体、计划刻、HSA；`color.cpp` 的方块颜色着色与图片导出已移植到 `BedrockRender`（见第 9 节）。

## 9. 区块渲染（BedrockRender + Avalonia）

`BedrockRender` 把解析后的 `Chunk` 渲染成**每列一个像素**的剖面图；`BedrockRender.Demo` 用 Avalonia 读取一个存档的**全部区块**（可按维度筛选），逐区块渲染后拼接到一张统一画布，既可在窗口中查看，也可直接导出 PNG。

### 9.1 三种视图模式

| 模式 | 含义 | 颜色来源 |
|------|------|----------|
| `Surface` | 顶面地图：取每列地表最高非空气方块 | `block_color.json` + 生物群系对水/草/叶的着色（`color.cpp` 的 `blend_color_with_biome`） |
| `Biome`   | 生物群系地图：取每列顶层生物群系 | `biome_color.json` |
| `Height`  | 高度地图：按维度 Y 范围归一化着色（蓝→绿→黄→红） | 列顶实心方块高度 |

### 9.2 运行示例

```bash
# 在窗口中查看（默认主世界、Surface 模式、每列 4 像素）
dotnet run --project src/BedrockRender.Demo/BedrockRender.Demo.csproj -- <存档目录>

# 直接导出 PNG（无显示，headless）
dotnet run --project src/BedrockRender.Demo/BedrockRender.Demo.csproj -- <存档目录> --save world.png

# 完整参数
dotnet run --project src/BedrockRender.Demo/BedrockRender.Demo.csproj -- <存档目录> \
    --dim 1 \            # 0=主世界(默认) 1=下界 2=末地
    --mode biome \       # surface(默认) | biome | height
    --scale 8 \          # 每列像素数（默认 4）
    --save out.png       # 启用 headless 导出
```

### 9.3 作为类库使用

```csharp
using BedrockLevel.Level;
using BedrockRender;

var level = new BedrockLevel.Level.BedrockLevel();
level.Open(@"D:\Saves\MyWorld");

var palette  = ColorPalette.LoadEmbedded();   // 或从文件 LoadFiles(...)
var renderer = new ChunkRenderer(palette);

foreach (var cp in level.ChunkPositions(dimension: 0))   // 按维度筛选
{
    var chunk = level.GetChunk(cp);
    if (chunk == null) continue;

    // 渲染成 16*scale × 16*scale 的 RGBA 缓冲
    var rc = renderer.Render(chunk, ViewMode.Surface, scale: 4);

    // 直接把 RGBA 贴到任意 BGRA 帧缓冲上
    rc.BlitTo(bgraSpan, canvasWidth, canvasHeight, destX, destY);
}
```

