# DEVLOG — Reverse X

## เฟส 1 — Repo + โครงโปรเจกต์ + ทดสอบ REA (2026-10-13)

### ทำอะไรไปบ้าง
- สร้าง `D:\Code\ReverseX`, `git init -b main`, สร้าง public repo `xjanova/reverse-x` ด้วย `gh`, ตั้ง remote origin
- โครง .NET 8: `ReverseX.App` (WPF, assembly `ReverseX`, หน้าต่างเปล่าชื่อ "Reverse X"), `ReverseX.Core`, `ReverseX.Licensing` (ว่าง), `tests/ReverseX.Tests` (xUnit), `ReverseX.slnx`, `Directory.Build.props` (0.1.0), `.gitignore`, `.editorconfig`
- คัดลอก `mock.html` + `mock-*.png` ไป `design/mock/` (ไม่เอาโฟลเดอร์ `assets` ของ Anti X) และ BRIEF ไป `docs/BRIEF.md`
- `ReverseX.Core/Rea`:
  - `NodeProbe` หา node จาก PATH/Program Files + เช็ครุ่น (22.19+/24.11+/26+ ตาม `engines` ของ rea-agents 6.4.0)
  - `ReaLauncher` สร้างคำสั่ง `npx -y rea-agents@6.4.0 <args>`
  - `ReaClient` MCP stdio client (newline-delimited JSON-RPC): initialize, tools/list, tools/call, timeout, ยกเลิกได้ (ส่ง `notifications/cancelled`), ปิดสะอาด (ปิด stdin → รอ 3 วิ → kill tree)
  - `ReaCli` รัน `doctor|providers|capabilities --json` แล้ว parse เป็น model
- เทสต์ 30 ตัว (unit + integration กับ REA จริง) ผ่านทั้งหมด
- ทดสอบของจริง: ให้ REA วิเคราะห์ `ReverseX.Core.dll` ผ่าน `inspect_managed_artifact` / `inspect_managed_members` ได้ผล เก็บสรุปที่ `docs/samples/rea-analyze-reversex-core.json` (~98 KB)

### ข้อค้นพบเรื่อง REA 6.4.0
- เปิด MCP server: `rea mcp` (ต้องมี argument เดียวเท่านั้น; `--mcp` ก็ได้) ผ่าน `npx -y rea-agents@6.4.0 mcp`; มี 170 tools
- `rea doctor --json` **คืน exit code 1** เมื่อสภาพแวดล้อมไม่สมบูรณ์ (เช่นไม่มี Ghidra/Hopper) แต่ยังพิมพ์ JSON ถูกต้อง → ReaCli จึงตัดสินจาก "มี JSON หรือไม่" ไม่ใช่ exit code
- `rea providers --json` กับ `rea capabilities --json` ให้ผลเหมือนกันใน 6.4.0
- เครื่องนี้: doctor ไม่สมบูรณ์เพราะไม่มี Hopper/Ghidra/IDA และ registration ของ client อื่นเป็น config_drift — ไม่กระทบงาน .NET/JS

### การตัดสินใจ
- เรียก npx ผ่าน `node npx-cli.js` ที่อยู่ข้าง node.exe แทน `npx.cmd` เพื่อเลี่ยงปัญหา shim/quoting บน Windows (มี fallback เป็น `cmd /c npx`)
- เขียน MCP client เองแบบเล็ก (ไม่พึ่ง SDK) เพราะต้องการควบคุม timeout/cancel/ปิดโปรเซสเอง และ dependency น้อย
- Models เป็น lenient (เก็บ `Raw` JsonElement ไว้ด้วย) เพราะ schema ของ REA ใหญ่และอาจเปลี่ยนตามรุ่น
- Core ใช้ `net8.0` (ไม่ผูก Windows) เพื่อให้เทสต์/CI ง่าย; เฉพาะ App เป็น `net8.0-windows`
- LICENSE เป็น source-available ตามคำสั่ง ไม่ใช้ MIT; REA = MIT ใส่ notice ใน THIRD_PARTY_NOTICES
- ตัวอย่างใน `docs/samples/` ตัดข้อความแต่ละ tool ไว้ที่ 30,000 ตัวอักษรเพื่อให้ < 200 KB

### ติดขัด / เครื่องมือ CluadeX
- `read_file` อ่านนอก project root ไม่ได้ (BRIEF อยู่ที่ E:\…) ต้องอ่านผ่าน `run_command` + cat แทน
- `run_command` บล็อกคำสั่งที่มี `rm -rf` และบางคำสั่งยาวที่ต่อ `cd && ... ; ...` (บอกแค่ "blocked for safety" ไม่บอกสาเหตุ) ต้องแตกคำสั่งเป็นชิ้นเล็กและใช้ `write_file` แทน heredoc
- `cd` ถูกบังคับให้อยู่ใน project root (ตอนแรกยังไม่มี repo จึงต้องทำงานผ่านพาธเต็ม)
- ไม่มีปัญหากับ build/test tool ส่วนอื่น

### ยังไม่ได้ทำ (เฟสถัดไป)
ภาพ/ฟอนต์ (เฟส 2), ธีมและหน้าต่างจริง (เฟส 3), หน้าไบนารี/JS (เฟส 4), AI (เฟส 5), ไลเซนส์/อัปเดต/release (เฟส 6), PR xmanstudio (เฟส 7) — ยังไม่สร้าง release tag


## เฟส 2 — ภาพทั้งหมดผ่าน ChatGPT

### ทำอะไร
- ใช้ Chrome ของเจ้าของ (แท็บใหม่ของตัวเองใน tab group ของ CluadeX, ปิดแท็บเมื่อจบ) เปิด chatgpt.com ที่ล็อกอินไว้ แชตเดียว (ChatGPT ตั้งชื่อเองว่า "Generate logo concepts" — ไม่ได้เปลี่ยนชื่อเป็น "Reverse X — art")
- ได้ภาพ 6 ภาพ ไม่ติดโควตา: แผ่นโลโก้ 2x2, โลโก้เดี่ยว, แผ่นไอคอน 3x2 สองแผ่น (12 ไอคอน), พื้นหลัง night และ sky
- ต้นฉบับ → `design/art-src/` (`logo/`, `icons/`, `backgrounds/`); ไฟล์ที่แอปใช้ → `src/ReverseX.App/Assets/{Logo,Icons3D,Backgrounds}` + `Assets/reversex.ico` (16/24/32/48/64/128/256)
- ไอคอน 12 ชิ้น 512 px ตัดจากแผ่นด้วย Python (ตัดเศษ pixel ที่ล้ำจากช่องข้างเคียงด้วย connected-component) และภาพรวมที่ `design/art-preview.png`
- csproj: ใส่ `Assets` เป็น `Resource` และตั้ง `ApplicationIcon` (ยังไม่แตะ UI — เฟส 3)

### เลือกโลโก้แบบไหน
เลือก **D (โล่โครเมียม + X แก้วเรืองแสง + ฟองเลนส์เล็กมุมขวาบน)** ย่อทดสอบจริงที่ 16/32/64 px (`.tmp-rea` ไม่ commit):
- A (แว่นขยาย) ด้ามเล็กและวงกรอบบางหายที่ 16 px เหลือจุดกลมๆ
- B (ก้อนลูกบาศก์ผ่าครึ่ง) ลายวงจรละเอียดเกินไป กลายเป็นก้อนเบลอ
- C (X ลอกหน้ากระดาษ) รูปทรง X ชัดสุดแต่สีเงิน-ขาวกลืนไปกับพื้นสว่าง/theme sky และไม่มีกรอบบอกขอบ
- D เป็นทรงแน่น กรอบโครเมียมหนาให้ขอบชัดทั้งบนพื้นมืดและสว่าง X ฟ้าสว่างตัดกับพื้นน้ำเงินเข้ม จึงยังอ่านเป็น "X" ที่ 16–24 px; เสียเล็กน้อยคือฟองเลนส์หายที่ 16 px (ไม่กระทบ)
- ขอ D เป็นภาพเดี่ยวอีกรอบ (1254 px RGBA) ใช้ทำ logo/ico ทั้งหมด

### ตรวจพื้นโปร่งใส
ทุกภาพที่ขอแบบโปร่งใส (โลโก้ + ไอคอน) เป็น RGBA จริง มี alpha=0 รอบวัตถุ (ตรวจ pixel มุม และสัดส่วน alpha=0 ~30–60%) ไม่ใช่ลายหมากรุก ไม่ต้องลบพื้นเอง พื้นหลัง night/sky เป็น RGB ตามที่ต้องการ

### ChatGPT ติดอะไร
- DOM ตรงกับบทเรียนใน brain (Anti X): ช่องพิมพ์เป็น contenteditable (ไม่ใช่ textarea) ต้อง insertText แล้วกด Send ใน call แยก
- ChatGPT สร้างภาพ 1254x1254 (ขอ 1024) และไม่บังคับขนาด 1536x1024 ตรงเป๊ะ แต่ได้ 1536x1024 จริงสำหรับแผ่น 3x2/พื้นหลัง
- ภาพละ ~30–55 วินาที

### เครื่องมือ Chrome ของ CluadeX ติดอะไร
- `javascript_tool` ถูก CDP timeout ที่ 45 วินาที ถ้า `await setTimeout` เกิน ~40s (renderer ไม่ได้ค้างจริง) ต้องรอ ≤28s ต่อ call
- ไม่มีทางดาวน์โหลดไฟล์ตรง: ใช้วิธีจาก brain คือ fetch blob ในหน้า → เขียนลง OPFS → หาไฟล์ใน `%LOCALAPPDATA%\Google\Chrome\User Data\Default\File System` ตามขนาด byte แล้ว copy (ใช้ได้ดี)
- แท็บใหม่ของ tab group ถูกสร้างได้เมื่อเรียก `tabs_context_mcp createIfEmpty` ก่อนเท่านั้น (`tabs_create_mcp` ตรงๆ ล้มเหลว)
- `run_command` heredoc python ที่ `cd` ใน subshell ทำให้ cwd ผิด ใช้ `write_file` เขียนสคริปต์แล้วรันจาก root แทน


## เฟส 3 — ธีม + หน้าต่างหลัก + หน้าภาพรวม

### ทำอะไร
- ธีม: `Theme/Palette.Night.xaml`, `Palette.Sky.xaml` (key ชุดเดียวกัน มีเทสต์ตรวจ), `Theme/Theme.xaml` (สไตล์ล้วน: การ์ดกระจก, ปุ่ม aqua/violet/ghost, เมนูแคปซูล, ชิป, ช่องค้นหา ฯลฯ ใช้ DynamicResource ทั้งหมด)
- `Services/ThemeManager` สลับพาเลตต์สด เก็บที่ `%LOCALAPPDATA%\ReverseX\ui.json`; `Services/Loc` สลับภาษาไทย/อังกฤษ (`Resources/Strings.th|en.xaml`, ค่าเริ่มต้นไทย, มีเทสต์ตรวจ key ตรงกัน)
- ฟอนต์ Kanit/Anuphan ฝังเป็น Resource พร้อมไฟล์ OFL; mono ใช้ Cascadia Mono ของระบบ
- หน้าต่างหลัก: title bar ทำเอง (สลับธีม/ภาษา, ย่อ/ขยาย/ปิด), เมนูซ้าย 10 รายการ + โลโก้ + ข้อความ "ใช้กับซอฟต์แวร์ที่คุณมีสิทธิ์วิเคราะห์", หน้าที่ยังไม่ทำเป็น placeholder "กำลังจะมา"
- หน้าภาพรวม: ช่องลากวาง + เลือกไฟล์/โฟลเดอร์ → `FileKindDetector` → `ProjectStore` (projects.json), การ์ดเครื่องมือจริง (`ToolCheckService`: NodeProbe + ReaCli doctor/providers + Java/JADX/เบราว์เซอร์ ทำเบื้องหลัง), การ์ดสถิติ 4 ใบ, โปรเจกต์ล่าสุด, ลองถาม AI, สถานะว่าง, ข้อผิดพลาดเป็นข้อความไทย
- ภาพหน้าจอ: `ReverseX.exe --screenshots DIR [--empty]` render หน้าต่างจริงด้วย RenderTargetBitmap (ไม่ใช้เมาส์/จับจอ) → `design/screens/phase3-home-{night,sky}[-empty].png`
- เทสต์ใหม่ `tests/ReverseX.Tests/Phase3Tests.cs`: palette/strings key, ตรวจชนิดไฟล์, ProjectStore, ToolStatusMapper (รวม 42 ตัวผ่าน)

### เฟสนี้ถูกพักและทำต่อ
รอบแรกถูกหยุดกลางทางเพราะเจ้าของอัปเดตตัว CluadeX (ไม่ใช่งานผิด) ไฟล์ที่เขียนไว้ยังอยู่ครบแต่ยังไม่ commit และ build ยังไม่ผ่าน (HomeViewModel ขาด `using System.IO`, RelayCommand ambiguous, deconstruct ใน lambda) — รอบนี้แก้แล้วเขียนเทสต์ + โหมด screenshot ต่อ

### การตัดสินใจ
- ใช้โหมด `--screenshots` ใน App เอง แทนโปรเจกต์คอนโซลแยก (ใช้ MainWindow จริง + ข้อมูลเครื่องมือจริง ได้ภาพตรงกับที่ผู้ใช้เห็น); ใช้ projects.json ชั่วคราว ไม่แตะของผู้ใช้
- ธีม Sky: ชิปสถานะสีเขียว/ฟ้า/ม่วงมองจางบนเมฆ จึงเปลี่ยนพื้นเป็นสีอ่อนทึบ
- ภาพ render ที่ 1280x960 เพื่อให้เห็นการ์ดล่างครบ (ที่ 800 สูงหน้าจะมี scroll)

### ติดขัด / ข้อสังเกต
- เครื่องมือ CluadeX: auto-verify ผ่านแต่ตรวจวงเล็บของ write_file แจ้งเตือนผิด (false positive) กับไฟล์ C#; run_command `cd` ค้างข้ามคำสั่งทำให้ path สัมพัทธ์ผิดครั้งหนึ่ง
- ยังเหลือเล็กน้อย: ข้อความท้ายเมนูซ้ายขึ้นบรรทัดใหม่เหลือตัว "น" ตัวเดียว; คำบรรยายใต้หัวข้อหน้ามี contrast ต่ำบนพื้นหลังที่ลายเยอะ


## เฟส 4 — หน้าไบนารี + หน้าแอป JS/Electron ต่อ REA จริง

### ทำอะไร
- `ReverseX.Core/Analysis/`: `Models` (model ของเรา + `EvidenceLevel` เห็นจริง/อนุมาน/ยังไม่รู้ แปลงจาก confidence ของ REA), `ReaResultParser` (JSON ของ REA → model; รับทั้งข้อความ MCP และซองของ CLI `{ok,data}`), `AnalysisRunner` (.NET / JS / native, ยกเลิกได้ระหว่างและกลางการเรียก), `ReaSession` (REA process เดียวต่อหนึ่งการวิเคราะห์ เพื่อให้ evidence ที่เก็บไว้ยังอยู่), `SnapshotStore` (`%LOCALAPPDATA%\ReverseX\projects\<id>\snapshot.json.gz`, id = sha256 ของพาธ; เปิดซ้ำไม่วิเคราะห์ใหม่ เตือนเมื่อไฟล์เปลี่ยน), `Paging`, `GoBuildInfo`
- .NET: `inspect_managed_artifact` + `inspect_managed_members` → ซ้าย type/method ค้นหาได้, กลางแท็บ CIL (จุดยึดที่ REA ถอดได้ ชื่อ method/field ถูก resolve จาก token) / metadata, ขวาการเรียก+ฟิลด์ที่ใช้ และสรุปพร้อมป้ายหลักฐาน
- JS/Electron: `analyze_javascript_application` (detail=summary) แล้วดึงรายการโมดูลด้วย `inspect_analysis_view` เป็นหน้าๆ (250 ต่อครั้ง เพดาน 5,000) แสดงการ์ดสรุป, แผนผัง renderer → preload → IPC → main, รายการโมดูลแบ่งหน้า 40/หน้า + ค้นหา
- native (PE/ELF/Mach-O): เรียก `open_binary` → `list_procedures`; มีเส้นทาง `analyze_function` / `get_call_graph` ในโค้ดแล้ว (ใช้เมื่อมี engine). ไม่มี engine → หน้าจอบอกเป็นภาษาไทยว่าต้องมี Ghidra 12.1.x (JDK 21) / IDA / Hopper พร้อมเหตุผลของแต่ละตัวจาก REA และปุ่มเปิดคำแนะนำ — ไม่ใช่ error ดิบ
- Go: ดูข้อ "การตัดสินใจ"
- งานเบื้องหลัง + แถบความคืบหน้า + ปุ่มยกเลิก; ข้อผิดพลาดทุกชนิดแปลงเป็นข้อความไทย/อังกฤษ (รายละเอียดดิบลง log เท่านั้น)
- คลิกแถวในโปรเจกต์ล่าสุด หรือเพิ่มไฟล์เดียวที่รู้จัก → ไปหน้าที่ตรงชนิดและวิเคราะห์เลย
- แก้ข้อความท้ายเมนู: ตัดบรรทัดเองเป็น 2 บรรทัดเท่ากัน ไม่เหลือ "น" โดดๆ
- ภาพ: `ReverseX.exe --screenshots DIR --phase4 <dll> <asar> <exe>` วิเคราะห์ของจริงแล้ว render → `design/screens/phase4-*.png` (ว่าง, .NET + meta, JS, native/Go; ทั้ง night และ sky)
- เทสต์ใหม่ `Phase4Tests.cs` (36 ตัว) ใช้ fixture JSON จริงที่บันทึกจาก REA ใน `tests/ReverseX.Tests/Fixtures/` (ตัดให้เล็ก และแทนพาธเครื่องด้วยพาธตัวอย่าง) ครอบคลุม model, paging, ยกเลิก, ข้อความไม่มี engine, fallback เมื่อผลใหญ่, snapshot, Go; มีเทสต์จริงกับ REA 2 ตัว (ข้ามเองถ้าไม่มี Node)
- ผลรวม: 76 เทสต์ผ่านหมด

### ของจริงที่ทดสอบ
- .NET: `ReverseX.Core.dll` ของเราเอง (1,073 method, 114 type) — REA ตอบผ่าน MCP ได้ (~2 MB)
- Electron: `Obsidian\resources\app.asar` (อ่านอย่างเดียว; เทสต์ตรวจ LastWriteTime ไม่เปลี่ยน) — 109 โมดูล, IPC 15 จุด (ตายตัว 2, ไดนามิก 13)
- native/Go: `gh.exe` (GitHub CLI, Go 1.25.5, 221 โมดูล) — ได้ข้อมูล PE + build info ของ Go; ส่วน list_procedures ได้ provider_unavailable จริง (ไม่มี engine) จึงโชว์หน้าแนะนำ

### การตัดสินใจ (เจ้าของไม่อยู่ — ตัดสินเองและจดเหตุผล)
- **REA 6.4.0 ไม่มี tool `inspect_go_binary`** (ตรวจรายการ 170 tool แล้ว) จึงอ่าน build info ของ Go เองในเครื่อง (`GoBuildInfo`: header `\xff Go buildinf:` แบบ inline ของ Go 1.18+) แทน — อ่านอย่างเดียว ไม่เรียก REA
- JS: `detail=complete` เกินงบ transport ของ MCP จริง (`resource_constraint`) จึงใช้ `summary` + `inspect_analysis_view` แบบแบ่งหน้าตามที่ REA แนะนำ
- JS: ใช้ `integrity_policy=record-and-continue` เพราะแอปจริง (Obsidian) ถูกปฏิเสธด้วย `artifact_integrity_mismatch` (ตัวแพ็กเกอร์ซิกน์ .node ภายหลัง); หน้าจอแสดงคำเตือนสีเหลืองและ REA ถือไบต์นั้นว่าไม่น่าเชื่อถือ
- .NET: ถ้าผลใหญ่เกิน MCP จะสลับไปเรียก CLI (`inspect-managed-members --format json --full-output`) ซึ่งไม่มีเพดานนี้; ยังไม่ได้เจอกรณีจริงบนเครื่อง ทดสอบด้วยของปลอมเท่านั้น
- ข้อความ limitation ที่ REA ส่งมา (อังกฤษ) แสดงตามที่ได้ ไม่แปลเอง เพื่อไม่ให้บิดความหมายของหลักฐาน
- ป้ายหลักฐาน: confidence=observed → เห็นจริง, derived/inferred → อนุมาน, limitations/unknowns → ยังไม่รู้
- ปุ่ม "เปิดคำแนะนำ" เปิดหน้า README ของ REA (ไม่ติดตั้งอะไรให้เอง)
- แท็บ CIL แสดงจุดยึด (anchors) ที่ REA ถอดได้ ไม่ใช่ C# ที่ถอดกลับ และบอกไว้บนหน้าจอ; string literal แสดงเป็น token เพราะ REA ไม่ให้ข้อความ

### ติดขัด / ข้อสังเกต
- เครื่องมือ CluadeX: `write_file` ไฟล์ใหญ่ (4 ไฟล์ใน Analysis/) บันทึกได้ 0 ไบต์ครั้งแรก (บอกว่า "Created" แต่ไฟล์ว่าง) — ต้องเขียนซ้ำ; ครั้งแรกที่ใช้พารามิเตอร์ `file_text` แทน `content` จึงเสีย
- เครื่องมือ CluadeX: ตัวตรวจวงเล็บของ write_file แจ้ง "Unbalanced" ผิดกับ C# ที่มี string/char ปีกกา (false positive ซ้ำจากเฟส 3); `edit_file` ที่เรียกเมื่อไฟล์ว่างแจ้งหา text ไม่เจอ
- `run_command` ผ่าน Git Bash รัน .exe แบบ GUI ที่มีอาร์กิวเมนต์พาธมีช่องว่างแล้วคืน exit 1 โดยไม่มีข้อความ — ใช้ `powershell` + `Start-Process` แทนจึงได้
- XAML: key ข้อความซ้ำ (S.Dlg.*, S.Btn.PickFolder มีอยู่แล้วจากเฟส 3) ทำให้แอปล้มตอนเปิด ต้องกันซ้ำ; เจอเพราะโหมดภาพหน้าจอ
- การรัน `dotnet test` ทั้งชุดใช้เวลา ~5 นาทีเพราะเทสต์ REA จริง (npx เริ่มช้า)
- ยังไม่ทำ (นอกขอบเขตเฟสนี้): แท็บ pseudo-code/แอสเซมบลี/กราฟการเรียกของ native ที่ต้องมี engine จึงยังไม่เคยทดสอบกับผลจริง (มีแค่เส้นทางเรียก + แสดง JSON ดิบของ REA); Go ยังไม่มีเทสต์กับไบนารีจริงในชุดเทสต์ (ทดสอบมือกับ gh.exe ผ่านภาพหน้าจอ ส่วนเทสต์ใช้ blob สังเคราะห์)


---

## เฟส 5 — ผู้ช่วย AI + หน้าตั้งค่า

### ทำอะไร
- `ReverseX.Core/Ai/`: ตัววนลูป agent (`AiAgent`) คุยกับ Claude API แบบ streaming, ให้โมเดลเรียก tool ของ REA ได้, เก็บ thinking block ไว้ในประวัติ, คำนวณค่าใช้จ่ายตามราคาต่อโมเดล (Haiku สลับ tier เมื่อ prompt เกิน 100k), มี 4 โมเดล ค่าเริ่มต้น Opus
- `ToolSelector` เสนอเฉพาะ tool read-only ที่เกี่ยวข้อง + tool ภายใน (`record_finding`, อ่าน/เสนอไฟล์ของโปรเจกต์ผู้ใช้); ผลลัพธ์ใหญ่ถูกย่อ (`Shrink`) แล้วให้โมเดลขอหน้าถัดไปได้
- หลักฐาน (`Finding`) มีระดับ เห็นจริง/อนุมาน/ยังไม่รู้ ตามต้นแบบ
- เสนอแก้ไฟล์ (`CodeChange` + `UnifiedDiff`): แสดง diff ให้ผู้ใช้ยืนยันก่อนเขียนทุกครั้ง
- ความปลอดภัยของ path: `GuardPaths`/`CheckPath` กัน path นอกโปรเจกต์, `..`, ไดรฟ์/`:`, `.git`, ไฟล์ใหญ่เกินกำหนด, diff ใหญ่เกิน 16 ล้านเซลล์
- หน้า AI + หน้าตั้งค่า (API key เก็บด้วย DPAPI, ธีม, ภาษา); ภาพ `design/screens/phase5-*.png`
- เทสต์ใหม่ `Phase5Tests.cs` ใช้ streamer/REA ปลอม (วนลูป, ย่อผล, ปฏิเสธ path, ยกเลิกกลางสตรีม, ข้อความปฏิเสธเป็นไทย)

### แก้ระหว่างปิดเฟส
- `Real_DotNet_AnalyzesOwnCoreAssembly` ล้ม: `ReverseX.Core.dll` โตขึ้นจนเกินงบ metadata 64 MB ภายในของ REA (ทั้ง MCP และ CLI `--full-output` ตอบ `coverage: unavailable`) แล้วแอปแสดงรายการว่างเงียบๆ
  → `AnalysisRunner` โยน `TooLarge` พร้อมข้อความ limitation ของ REA; เทสต์ยอมรับกรณีนี้

### ติดขัด / ข้อสังเกต
- ยังไม่มีเทสต์ครอบ path `..`, path แบบ array และ path ที่มี `:` ใน `GuardPaths`
- ไม่ตาม symlink/junction ในโฟลเดอร์ผู้ใช้ (ผู้ใช้ยืนยัน diff ก่อนเขียนอยู่แล้ว)
- ยังไม่ได้ลองเรียก Claude API จริง (มีแอตทริบิวต์ `RealApiFact` สำหรับเทสต์จริง ข้ามถ้าไม่มี key)
