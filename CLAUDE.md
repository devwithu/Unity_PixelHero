# CLAUDE.md

이 파일은 Claude Code(claude.ai/code)가 이 저장소에서 작업할 때 따를 안내입니다.

## 프로젝트 소개

**PixelHero**는 2D MMORPG입니다. **Unity 클라이언트**와 **C#(.NET 10) 게임 서버, 허브**로 이루어져 있습니다. **BrowserQuest**를 포팅하는 것으로 시작했지만 지금은 독립된 프로젝트이며, BrowserQuest에 **의존하지 않습니다**. 저장소의 어떤 것도 이제 원본을 실행하거나 읽거나 원본과 비교하지 않습니다. 코드는 여전히 원본의 구조를 따르고, 회귀 fixture는 원본에서 기록했습니다(아키텍처 참고). `FEATURES.md`에는 포팅한 게임 기능이, `HUB_PLAN.md`에는 허브, HTTP API, 리더보드, 비밀번호 재설정, Discord 포팅(완료)이, `live.md`에는 운영 배포(`hub.jdj.kr`, `pixelhero.jdj.kr`)가 정리되어 있습니다. 세 문서 모두 한국어입니다.

**이름:** 제품 이름은 어디서나 PixelHero입니다. 폴더, 네임스페이스(`PixelHero.*`), 어셈블리, 환경 변수(`PIXELHERO_*`), PlayerPrefs 키(`pixelhero.*`), 셰이더(`PixelHero/...`), 게임 안 텍스트와 월드 모두 그렇습니다. 새 코드와 텍스트도 이 이름으로 씁니다. "BrowserQuest"는 원작을 밝히는 곳에만 남깁니다. 크레딧 화면과 그 링크(`IntroView.OriginalUrl`, `credits.port`), 소개 텍스트, 오픈 소스에 대한 NPC 대사, 그리고 출처를 설명하는 주석이나 문서입니다. MongoDB 데이터베이스 이름도 `pixelhero`입니다. `Tools/fixtures/`의 fixture에도 같은 이름 변경을 적용했습니다.

## 구성

- `Assets/`, `Packages/`, `ProjectSettings/`: Unity 프로젝트입니다(Unity **6000.3.26f1**, 2D URP, 새 Input System, Test Framework, Newtonsoft Json, **Gamma** 색 공간). 루트의 `Library/`, `*.csproj`, `*.sln`은 Unity가 만드는 파일이므로 절대 `dotnet build`하지 않습니다.
- `Assets/PixelHero/`: 클라이언트 코드 전체입니다. 어셈블리는 `PixelHero.Runtime`(네임스페이스 `PixelHero`), `PixelHero.Editor`, `PixelHero.Tests.EditMode`, `PixelHero.Tests.PlayMode`입니다.
- `Server/`: `packages/server`와 `packages/hub`의 C# 포팅입니다(`PixelHero.Server.slnx`). `PixelHero.Server.Core`가 포팅 본체, `PixelHero.Server`가 SuperSocket 2.1 호스트, `PixelHero.Hub`가 허브, `tests/PixelHero.Server.Tests`가 NUnit 테스트입니다.
- `PixelHero-Data/`: 게임 데이터입니다. 원본의 `packages/` 단계 없이 배치했습니다. C# 서버는 `server/data/**`를 읽고(`ServerMain.ResetState`), 허브는 그중 `mobs.json`을 읽습니다. `common/text/profanity.json`은 채팅 필터입니다. `client/maps/map.json`은 클라이언트 맵이며, 임포터가 `Generated/data/map.json`으로 복사합니다. 이 폴더에는 `.env.defaults`와 배포용 샘플 `.env*.sample`도 있습니다. 두 서버 모두 `PixelHero-Data/server/data`를 찾아 저장소 루트를 정합니다. 아직 게임에 넣지 않은 퀘스트 초안은 `Docs/quest-drafts/`에 있습니다. `quests/*.json` 형식을 쓰지만 그 NPC와 아이템은 아직 없습니다. 퀘스트를 추가하려면 JSON을 `server/data/quests/`에 넣고, 그 키를 `Quests.cs` 맨 위 목록에 넣습니다.
- `Tools/serve_webgl.mjs`는 WebGL 빌드를 서빙합니다. `Tools/live_test_server.sh`와 `Tools/seed_test_accounts.mjs`는 라이브 PlayMode 테스트용 서버를 띄웁니다(명령어 참고). `Tools/fixtures/`에는 기록된 테스트 데이터만 있습니다. `out/`(기대 결과), `recordings/`, `sessions/`, `scenes/`, `server/scenarios/*.json`(시나리오 입력)입니다. 원본에서 이것들을 기록한 스크립트(`gen_*.mts`, `harness.mjs`, `build_scenarios.mjs`, `codegen/`, `net/`)는 삭제했습니다. 아직 그 이름이 남은 주석은 fixture의 출처를 말할 뿐입니다.

## 명령어

C# 서버와 허브(우리 프로젝트이므로 `dotnet`을 써도 됩니다):

```
dotnet build Server/PixelHero.Server.slnx
dotnet test Server/tests/PixelHero.Server.Tests                                   # 약 2분. 원격 DB에 접속할 수 없으면 Mongo 계약 테스트는 건너뜀
dotnet test Server/tests/PixelHero.Server.Tests --filter "FullyQualifiedName~ScenarioTests"
dotnet run --project Server/src/PixelHero.Server -- --port 9031 --skipDatabase true   # 설정: PixelHero-Data/.env.defaults, 그다음 .env, 그다음 --key value
dotnet run --project Server/src/PixelHero.Hub -- --hubEnabled true                    # WS는 HUB_WS_PORT, HTTP는 HUB_PORT
```

Unity는 **배치 모드**로 실행합니다. 에디터가 프로젝트를 열고 있으면 실패하므로 먼저 사용자에게 에디터를 닫아 달라고 요청합니다. 로그에서 `error CS`와 예외를 확인합니다.

```
UNITY=/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity
$UNITY -batchmode -projectPath "$PWD" -runTests -testPlatform EditMode -testResults results.xml -logFile tests.log
$UNITY -batchmode -projectPath "$PWD" -runTests -testPlatform EditMode -testFilter "PixelHero.Tests.GameVersionTests" -testResults results.xml -logFile tests.log
$UNITY -batchmode -projectPath "$PWD" -runTests -testPlatform PlayMode -testResults play.xml -logFile play.log
$UNITY -batchmode -quit -projectPath "$PWD" -executeMethod PixelHero.Editor.CatalogBuilder.RebuildBatch -logFile catalog.log   # 임포트 설정 + SpriteCatalog.asset
$UNITY -batchmode -quit -projectPath "$PWD" -executeMethod PixelHero.Editor.GameSceneBuilder.BuildBatch -logFile scene.log    # Scenes/Game.unity를 다시 씀
$UNITY -batchmode -quit -projectPath "$PWD" -executeMethod PixelHero.Editor.WebGLBuilder.BuildBatch -logFile webgl.log        # Builds/WebGL (약 7분)
node Tools/serve_webgl.mjs [9050] [--https]   # 빌드 서빙. ?server=ws://host:port, ?hub=http(s)://host:port
```

`unity` CLI(unity-cli 스킬 / Unity MCP)로 실행 중인 에디터를 조작할 수도 있습니다.

EditMode 테스트는 게임 데이터를 `TestPaths.Data(...)`로, 기록된 fixture를 `TestPaths.Fixtures`/`Recordings`로 읽습니다. `LiveServerTests`/`LiveGameTests`(PlayMode)는 서버가 떠 있지 않으면 건너뜁니다. 이 테스트들은 `PixelHero-Data/.env`의 PORT나 `PIXELHERO_SERVER_URL`(예: `ws://127.0.0.1:9031`)을 씁니다.

라이브 PlayMode 테스트(`LiveGameTests`)에는 데이터베이스(길드, 친구)가 있는 서버와 관리자 계정이 필요합니다. 운영 데이터베이스가 아니라 로컬 MongoDB 컨테이너 `pixelhero`로 돌립니다. `PixelHero-Data/.env`는 운영 데이터베이스와 허브를 가리키고, `Tools/live_test_server.sh`가 그 둘(데이터베이스, 허브, API, Discord)을 덮어씁니다. `LiveGameTests`는 씬의 `HubUrl`을 비우므로 `ServerUrl`에만 접속합니다(허브를 일부러 쓰려면 `PIXELHERO_HUB_URL`을 줍니다).

```
# 처음 한 번: 127.0.0.1:27017의 MongoDB 4.4와 pixelhero 사용자(비밀번호 pixelhero, 로컬 전용)
docker run -d --name pixelhero -p 127.0.0.1:27017:27017 -v pixelhero-mongo:/data/db -e MONGO_INITDB_ROOT_USERNAME=root -e MONGO_INITDB_ROOT_PASSWORD=pixelhero-root mongo:4.4
docker exec pixelhero mongo -u root -p pixelhero-root --authenticationDatabase admin --eval 'db.getSiblingDB("pixelhero").createUser({user:"pixelhero", pwd:"pixelhero", roles:[{role:"readWrite", db:"pixelhero"}]})'
Tools/live_test_server.sh             # 컨테이너가 꺼져 있으면 켬. 포트 59011(Game 씬의 ServerUrl)
node Tools/seed_test_accounts.mjs     # 처음 한 번, 서버가 켜진 상태에서: unityadmin(rank 2, .test-account.json)과 unitytest1
```

테스트할 때마다:
1. `Tools/live_test_server.sh`를 실행하고 "Successfully connected to the MongoDB server"가 나올 때까지 기다립니다. 로그에 "hub"가 들어간 줄이 없어야 합니다.
2. 에디터를 닫고 PlayMode 테스트(`$UNITY ... -testPlatform PlayMode`)를 돌립니다. 15개 모두 통과해야 합니다.
3. 서버를 끕니다(Ctrl+C). 컨테이너는 켜 둬도 되며, `docker stop pixelhero`로 끌 수 있습니다.

커밋된 `Assets/PixelHero/Generated/`가 스프라이트, 타일셋, 폰트, 오디오, `sprites.json`, 지역 데이터의 원본입니다. 에셋을 바꾸려면 그곳에서 .meta와 함께 추가, 교체, 삭제합니다. 그다음 `CatalogBuilder.Rebuild`(메뉴 **PixelHero/Rebuild Sprite Catalog**)를 실행합니다. 임포트 설정을 적용하고, `PixelHero-Data/client/maps/map.json`을 `Generated/data/map.json`으로 복사하고, `SpriteCatalog.asset`을 다시 씁니다.

## 아키텍처 (큰 그림)

**회귀 fixture.** `Tools/fixtures/`의 fixture는 원본 PixelHero에서 기록했습니다. 원본은 가상 타이머와 시드를 준 `Math.random`(Park–Miller)으로 돌았습니다. fixture는 현재 동작을 바이트 단위로 고정합니다.
- 클라이언트는 엔티티, 텍스트, 오디오, 오버레이 재생 테스트(`EntityReplayTests`)를, 서버는 패킷 프레임 시나리오(`ScenarioTests`, 그리고 여러 서버와 허브용 `ClusterScenarioTests`)를 씁니다.
- fixture는 더 이상 다시 만들 수 없습니다. 동작을 일부러 바꾸는 변경(게임 규칙, 데이터, 텍스트)은 영향받는 `Tools/fixtures/out/`의 기대 결과를 같은 변경에서 갱신해야 합니다. 이때 diff가 의도한 것과 정확히 같은지 확인합니다.
- 의도한 변경 없이 실패하는 테스트는 회귀입니다.

**JS 의미 계층(`Scripts/Runtime/Js/`)**은 클라이언트와 서버가 함께 씁니다. `PixelHero.Server.Core.csproj`가 `Assets/`의 `JsJson.cs`, `JsNumber.cs`, `JsValue.cs`, `Fdlibm.cs`, `Common/Generated/*.cs`를 링크합니다. 이 파일들은 `Assets/` 아래에서만 고치고, Unity와 .NET 양쪽에서 컴파일되게 유지합니다.
- `JsNumber`는 10진 문자열과 double 사이를 정확하게 변환합니다. Mono의 `double.Parse`는 올바르게 반올림하지 않으므로, 통신으로 오가는 숫자에는 절대 쓰지 않습니다.
- `Fdlibm`은 V8과 똑같은 sin/cos/atan2를 줍니다. 원본이 계산하는 값에는 절대 `Math.Sin/Cos/Atan2`를 쓰지 않습니다.
- 서버에서 `JsObject`는 JavaScript의 키 순서를 유지하고, `EventLoop`/`Node`가 setTimeout, setImmediate, 마이크로태스크, `Date.now`, `Math.random`을 제공합니다. 타이머는 원본과 같은 순서로 만들어야 합니다. 같은 시각의 타이머는 만든 순서대로 실행되기 때문입니다.
- undefined는 C#의 null이나 `double?`가 됩니다. JSON null은 `JsonNull`입니다. 원본이 TypeError를 던지는 곳에서는 예외(`JsTypeError`)를 던집니다.
- 생성자 순서: esbuild는 `super()` 뒤에 클래스 필드를 매개변수 속성보다 먼저 초기화합니다. 부수 효과가 있는 것(타이머, 난수, 다른 필드 읽기)은 C# 생성자 본문 맨 위에 둡니다.
- `Scripts/Runtime/Common/Generated/`(패킷, 옵코드, 모듈, 상수)는 한 번 생성한 뒤 지금은 손으로 관리합니다. `CommonEnumsTests`가 이것을 `Tools/fixtures/out/common_enums.json`과 비교하므로, 프로토콜을 바꾸면 둘 다 갱신합니다.

**클라이언트(`Assets/PixelHero/Scripts/Runtime/`)**는 원본 클라이언트를 모듈 단위로 따르고 이름도 유지합니다.
- `Network`: `IWebSocketTransport` 뒤의 전송 계층(PC는 ClientWebSocket, WebGL은 jslib. 이벤트는 메인 스레드에서 전달), `PacketFrames`, `GameConnection`(Welcome까지의 세션).
- `Maps`: `ClientMap`, `RegionCache`, `TileDrawPlanner` → `MapView`. 원본 좌표(왼쪽 위 원점, y 아래 방향)와 Unity 좌표 사이의 변환은 **오직** `MapCoords`에서만 합니다.
- `Entities`: `World`가 원본의 `game`을 대신합니다. `EntityPackets`에 엔티티 패킷 핸들러가 있습니다. `EntityDrawPlanner`/`TextDrawPlanner`가 그리기 명령을 기록하고, `EntityCanvasRenderer`(`Shaders/CanvasImage.shader` 사용)와 `UI/TextCanvasView`가 브라우저 캔버스를 흉내 내어 재생합니다. 그리기는 픽셀이 아니라 기록된 호출로 비교합니다. `GameClient`는 `EntityPackets.Handled`에 있는 패킷만 `EntityPackets.Handle`로 넘기므로, 새 패킷은 둘 다에 추가합니다.
- `UI`: `UiKit`이 원본의 HTML/CSS를 uGUI 코드로, 정수 배율의 CSS 픽셀 단위로 만듭니다. `Localization`은 `Localization/{ko,en}.json`을 읽으며 기본은 한국어입니다. `PixelText`는 한글에 Galmuri를 씁니다.
- `Sprites/SpriteCatalog`가 임포트한 에셋의 색인입니다. 프레임은 실행 중에 잘라 냅니다.
- `Assets/PixelHero/Generated/`에 임포트한 에셋과 `SpriteCatalog.asset`이 있습니다. 씬은 이것들을 GUID로 참조하므로, 옮기거나 바꿀 때는 .meta 파일과 함께 다룹니다.

**서버(`Server/src/PixelHero.Server.Core`)**는 원본의 구조, 이름(필드는 camelCase 유지, 메서드는 PascalCase), 주석을 유지합니다. 숫자는 double이고, JSON 데이터는 JSON 모양 그대로 다룹니다.
- 저장소는 `IGameDatabase` → `GameDatabase` → `IDocumentStore` 순으로 이어집니다(테스트는 `MemoryDocumentStore`, 서버는 `MongoDocumentStore`). 게임 코드는 MongoDB 타입을 보지 않습니다. 사용자가 나중에 PostgreSQL로 옮길 계획입니다. `DocumentStoreContractTests`가 모든 저장소에 같은 케이스를 돌립니다.
- MongoDB.Driver는 **3.9.0**으로 유지합니다. 3.10 이상은 사용자의 MongoDB 서버를 거부하고, 3.4–3.8은 취약한 의존성을 끌어옵니다.
- 허브, HTTP API, Discord(`Network/Hub.cs`, `Network/Express.cs`, `Controllers/Incoming.cs`)는 `HUB_ENABLED`/`API_ENABLED`/`DISCORD_ENABLED`가 설정되지 않으면 꺼져 있습니다. 꺼져 있을 때는 타이머, 소켓, 로그 줄을 하나도 만들지 않아야 시나리오가 바뀌지 않습니다.
- `ClusterHarness`의 각 가상 서버는 서버별 정적 상태를 담는 `Realm`을 가집니다. 서버별 정적 상태를 새로 만들면 `Realm`에 추가해야 합니다.
- `Data/map_fixes.json`은 world.json에 빠진 엔티티와 오브젝트 타일(튜토리얼 나무, 새우 낚시터, 요리 냄비)을 추가합니다. 이것을 바꾸면 시작 시 인스턴스가 밀리므로 `startup.json`과 시나리오 fixture도 함께 바뀝니다.
- TLS: `SSL_PORT`와 `SSL_CERTIFICATE`가 설정되면 wss를 제공합니다(`Network/TlsCertificate`. .pfx 또는 PEM + `SSL_CERTIFICATE_KEY`). https 페이지의 Unity 클라이언트는 항상 59443의 wss로 접속합니다(`live.md` 참고).

## Unity 규칙

- 새 Input System(`UnityEngine.InputSystem`)만 쓰고, `Input.*`는 절대 쓰지 않습니다.
- 게임 로직은 Unity 물리가 아니라 데이터 격자를 씁니다.
- 없는 컴포넌트는 가짜 null이라서 `GetComponent<T>() ?? AddComponent<T>()`는 아무것도 추가하지 않습니다. `TryGetComponent`를 씁니다.
- `CommandBuffer.SetViewProjectionMatrices`는 렌더 텍스처에 대해 이미 뒤집기를 합니다. `GL.GetGPUProjectionMatrix`를 추가하지 않습니다.
- 블렌드 모드는 렌더 상태입니다. 캔버스 합성 연산마다 별도의 머티리얼을 줍니다(`EntityCanvasRenderer.CanvasMaterial`).
- 배치 모드 PlayMode 테스트에서는 `WaitForEndOfFrame`/`ScreenCapture`가 동작하지 않으므로 RenderTexture에 렌더링합니다. 입력을 주입하는 테스트는 `InputSystem.settings.backgroundBehavior = IgnoreFocus`가 필요합니다.

## 비밀 정보와 환경

- `PixelHero-Data/.env`(gitignore 대상)에 MongoDB 연결을 포함한 로컬 설정이 있습니다. 데이터베이스와 인증 소스는 둘 다 `pixelhero`입니다. `.env*.sample`에는 실제 `HUB_ACCESS_TOKEN`이 들어 있습니다. `.env`나 샘플의 값을 절대 출력하지 않습니다.
- dotenv는 줄 끝의 `# 주석`을 값의 일부로 취급하므로, `.env`의 값이 있는 줄에는 절대 주석을 달지 않습니다.
- 서버는 `DEBUGGING=true`가 아니면 같은 IP에서 5초 안에 다시 접속하는 것을 거부합니다(`toofast`). 자동 접속 테스트에서 중요합니다.
- `SKIP_DATABASE=true`이면 아무것도 저장하지 않고 모든 플레이어가 관리자가 되어, 시나리오에서 관리자 채팅 명령(`/mob`, `/teleport x y`, `/kill` 등)이 동작합니다. 데이터베이스를 켜면 관리자 명령은 `player_info`에 rank가 설정된 계정만 쓸 수 있습니다.

## 사용자와 일하는 방식

- **한국어**로 소통합니다. 프로젝트 문서와 이 CLAUDE.md도 한국어로 씁니다.
- 큰 작업은 단계로 나눠 계획하고, 단계마다 확인 방법을 합의하며, 단계가 끝날 때마다 짧게 보고합니다. 사용자가 아직 시작하지 말라고 하면 계획만 세웁니다.
- 브라우저 자동화나 헤드리스 브라우저를 **쓰지 않습니다**. 대신 사용자가 브라우저에서 확인할 순서를 적어 드립니다.
- git 작업은 모두 사용자가 합니다(커밋과 푸시는 요청이 있을 때만 합니다).
