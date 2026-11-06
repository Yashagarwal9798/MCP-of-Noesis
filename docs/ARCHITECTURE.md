# Architecture

Two deliverables share one idea: **the agent only writes XAML, and Noesis itself decides whether it is right.**

Colour key in every diagram: blue = host, green = code we write, grey = things we didn't write (MCP SDK, Noesis SDK), orange = `render_xaml` (the demo highlight).

## 1. MCP-of-Noesis: component view

```mermaid
flowchart LR
    U([You: prompt]) --> LLM

    subgraph HOST["MCP Host: Claude Code / Cursor / Copilot"]
        LLM["LLM (Claude)"] <-->|tool call / result| CLI["MCP client"]
    end

    CLI -- "stdin: JSON-RPC requests (1 per line)" --> PH
    PH -- "stdout: JSON-RPC responses only" --> CLI
    SRV -. "stderr" .-> LOG[(logs)]

    subgraph SRV["noesis-mcp (C# .NET 10 process)"]
        direction LR
        ST["Startup (Program.cs)\nlogs→stderr · stdio · register tools\ninit Noesis once · load docs once"]
        PH["Protocol handler\n(ModelContextProtocol SDK)"] --> REG["Tool registry\nschemas from [McpServerTool]"]
        REG --> EB["Dispatcher + error boundary\n(try/catch → isError result)"]
        EB --> T1["validate_xaml\nXAML → OK | Line,Col errors"]
        EB --> T2["render_xaml\nXAML,w,h → PNG"]
        EB --> T3["search_docs\nquery → ≤5 snippets"]
        EB --> T4["list_controls · wpf_differences\n(stretch)"]
        T1 --> WQ
        T2 --> WQ
        T4 --> WQ
        WQ["Noesis work queue\n1 dedicated STA thread"]
    end

    WQ --> ENG["NoesisEngine (wrapper)\nInit once (eval mode)\nValidate: ParseXaml + collect errors\nRender: D3D11 offscreen → PNG"]
    ENG --> NSDK["Noesis.GUI SDK\n(native C++ via P/Invoke)"]
    ENG --> D3D["D3D11 device (SharpDX)\nhardware → WARP fallback"]
    T3 --> DI["DocsIndex (in memory)\nBM25 + title boost"]
    DI --> DJ[(data/docs.json)]
    FS["scripts/fetch-docs.py\n(run once, local only)"] -.-> DJ

    classDef host fill:#dbeafe,stroke:#2563eb,color:#111
    classDef ours fill:#dcfce7,stroke:#16a34a,color:#111
    classDef ext fill:#e5e7eb,stroke:#6b7280,color:#111
    classDef accent fill:#ffedd5,stroke:#ea580c,stroke-width:3px,color:#111
    class LLM,CLI host
    class ST,REG,EB,T1,T3,T4,WQ,ENG,DI,FS ours
    class PH,NSDK,D3D ext
    class T2 accent
```

## 2. One `render_xaml` call, end to end

```mermaid
sequenceDiagram
    autonumber
    participant L as LLM
    participant C as MCP client
    participant P as Protocol handler (SDK)
    participant T as XamlTools
    participant Q as Work queue (Noesis thread)
    participant E as NoesisEngine
    participant N as Noesis SDK + D3D11

    L->>C: render_xaml(xaml, 1280, 720)
    C->>P: {"jsonrpc":"2.0","id":9,"method":"tools/call",...}
    P->>T: RenderXaml(xaml, 1280, 720)
    T->>T: limits check (≤200 KB, ≤3840×2160)
    T->>Q: enqueue job
    Q->>E: Validate(xaml)
    E->>N: GUI.ParseXaml(xaml) + log/error handlers
    alt XAML invalid
        N-->>E: errors
        E-->>T: ["Line 3, Col 9: Unknown type 'Buton'"]
        T-->>P: text content, isError=true
    else XAML valid
        E->>N: new View(root) · SetSize · Renderer.Init(RenderDeviceD3D11)
        E->>N: Update(t) · UpdateRenderTree · RenderOffscreen · Render → RT texture
        E->>N: CopyResource → staging · Map → BGRA pixels
        E->>E: encode PNG
        E-->>T: PNG bytes + render ms
        T-->>P: image content (base64 PNG) + text line
    end
    P-->>C: JSON-RPC result on stdout
    C-->>L: result
```

## 3. Process model: v1 (built) and v2 (robust)

```mermaid
flowchart LR
    subgraph V1["v1: single process (what we build first)"]
        A1[MCP server] --> B1[Noesis thread + lock]
    end
    subgraph V2["v2: worker process"]
        A2[MCP server] -- "JSON over pipes" --> B2[Noesis worker]
        A2 -. "restart on native crash\nor every N minutes (eval limit)" .-> B2
    end
```

- **Threads:** Noesis objects are single-threaded. Every Noesis call runs on one dedicated thread, which also owns the D3D11 immediate context.
- **Crashes:** native C++ crashes can't be caught in .NET. v1 accepts this. v2 moves Noesis into a child process so the server can answer "XAML crashed the renderer" and restart it.
- **Eval limit:** in evaluation mode Noesis runs 10 minutes per session. If rendering stops after that, the v2 worker is recycled.

## 4. Render pipeline detail

```mermaid
flowchart LR
    X[XAML string] --> P[GUI.ParseXaml] --> R[FrameworkElement root]
    R --> V[new View root] --> S[SetSize w×h · SetFlags PPAA]
    S --> I[Renderer.Init RenderDeviceD3D11 ctx]
    I --> U[View.Update t · Renderer.UpdateRenderTree]
    U --> O[Renderer.RenderOffscreen]
    O --> RT[Bind RT texture + depth/stencil · Clear · Viewport]
    RT --> RR[Renderer.Render]
    RR --> CP[CopyResource → staging texture]
    CP --> M[Map → BGRA rows] --> PNG[PNG encoder] --> B64[base64 image content]
```

- Render target: `B8G8R8A8_UNorm`, 1 sample. Stencil buffer `D24_UNorm_S8_UInt`, which Noesis needs for clipping.
- Theme: Noesis.App.Theme resources (NoesisTheme.DarkBlue) merged so standard controls (Button, Slider, CheckBox) have visuals, like the app does.
- PNG: a small built-in encoder (zlib from `System.IO.Compression`), so no image library is needed.

## 5. Docs index

```mermaid
flowchart LR
    W[noesisengine.com/docs/*.html] -- "fetch-docs.py (run once)" --> H[HTML pages] --> C[split by heading\n≈300-word chunks] --> J[(data/docs.json\nlocal only, gitignored)]
    J --> D[DocsIndex.Load at startup] --> Q[BM25 over title + body\ntitle ×3 boost] --> R[top N: title · url · ≤500-char snippet]
```

## 6. Offer Letter Catcher (Phase 2)

```mermaid
flowchart TB
    subgraph APP["OfferLetterCatcher.exe (.NET 10)"]
        direction TB
        MAIN[Program: NoesisApp Application + Win32 display + D3D11 render context]
        LOOP[GameLoop\nfixed 60 Hz · spawn · move · collide]
        VM[GameViewModel\nScore · Lives · State · Letters · PlayerX\nPlay/Pause/Resume/Quit commands]
        IN[Input: KeyDown/KeyUp on root]
        MAIN --> LOOP
        IN --> LOOP --> VM
    end
    subgraph XAML["XAML (written by the agent through MCP-of-Noesis)"]
        MW[MainWindow.xaml: state switcher]
        MM[MainMenu.xaml]
        HUD[Hud.xaml]
        PZ[PauseSettings.xaml]
        HR[Hired.xaml: Storyboard + confetti]
        GO[GameOver.xaml]
        PF[Playfield: ItemsControl over Canvas\nbound to Letters]
    end
    VM <-->|data binding| MW
    MW --> MM & HUD & PZ & HR & GO & PF
    AG[Claude Code + MCP-of-Noesis] -. "validate → fix → render" .-> XAML

    classDef ours fill:#dcfce7,stroke:#16a34a,color:#111
    classDef agent fill:#ffedd5,stroke:#ea580c,color:#111
    class MAIN,LOOP,VM,IN ours
    class MW,MM,HUD,PZ,HR,GO,PF agent
```

Game states: `Menu → Playing ⇄ Paused → Hired | GameOver → Menu/Playing`. The XAML switches screens with `DataTrigger`s on `State`. No code-behind decides what is visible.

## 7. Repository layout

```
MCP-of-Noesis/                     (separate GitHub repo)
  src/NoesisMcp/
    Program.cs                     startup, stdio, DI
    Tools/XamlTools.cs             validate_xaml, render_xaml, list_controls
    Tools/DocsTools.cs             search_docs, wpf_differences
    Engine/NoesisEngine.cs         the only code that touches Noesis
    Engine/NoesisThread.cs         one-at-a-time work queue
    Engine/D3DOffscreen.cs         headless D3D11 target + readback
    Engine/Png.cs                  minimal PNG encoder
    Engine/ErrorFormat.cs          "Line, Col: message" + hints
    Docs/DocsIndex.cs              BM25 search
  scripts/fetch-docs.py            builds data/docs.json locally
  tests/NoesisMcp.Tests/           xUnit over tests/xaml corpus
  tests/xaml/{valid,invalid,wpf-only}/
  tests/smoke.py                   stdio protocol smoke test
  docs/                            renders, GIF
  README.md

offer-letter-catcher/              (game, built with the MCP)
  src/OfferLetterCatcher/
    Program.cs  App.xaml  MainWindow.xaml(.cs)
    Game/GameLoop.cs  Game/GameViewModel.cs  Game/Letter.cs
    Views/*.xaml                   agent-written screens
  docs/agent-log.md  docs/renders/*.png
```
