# 허브·HTTP API·순위표·비밀번호 재설정·Discord 이식 계획

> **상태: 완료(2026-10-10).** 1일차 구현, 2일차 테스트, 사용자 확인(브라우저, 실제 메일, Discord)을 마쳤습니다. 결과와 원본과 다른 점은 PROGRESS.md의 "허브·HTTP API…(1일차)"와 "허브 등 2일차" 절에 있습니다. 아래는 당시의 계획으로 남겨 둡니다.

서버 이식(T0~T8) 때 일부러 뺀 부가 기능들을 옮기는 계획입니다. 2026-10-09 사용자와 정했고, **새 세션에서 이 문서를 읽고 시작합니다.** 먼저 `CLAUDE.md`, `PORTING_PLAYBOOK.md`, `PROGRESS.md`(특히 T0~T8, "다음 할 일")를 읽습니다.

## 진행 방식 (사용자 결정, 2026-10-09)

- **1일차: 구현만 합니다.** 새 테스트는 만들지 않습니다.
  - 빌드가 되는지, 기존 테스트(서버 68개, Unity EditMode)가 그대로 통과하는지만 확인합니다. 기존 테스트가 깨진 채로 넘기면 원인 찾기가 어려워집니다.
  - 구현한 코드는 테스트 전이므로 **커밋하지 않습니다**(커밋과 푸시는 사용자가 요청할 때).
- **2일차: 테스트합니다.** 원본과 비교하는 하네스 확장, 기능별 시나리오와 계약 테스트, 메일·Discord 실제 확인, 브라우저 확인을 합니다. 통과하면 커밋합니다.
- **모든 기능은 설정으로 꺼진 상태가 기본입니다.** 원본과 같은 설정 이름(`HUB_ENABLED`, `API_ENABLED`, `DISCORD_ENABLED` 등)을 씁니다. 켜지 않으면 지금 게임과 동작이 완전히 같아야 하고, 기존 시나리오 비교 결과도 바뀌지 않아야 합니다.
- 원칙은 지금까지와 같습니다. 원본 동작을 추측하지 말고 원본 코드를 읽고 옮깁니다. 구조, 이름, 주석도 원본을 따릅니다. 원본과 다르게 해야 하는 곳은 PROGRESS.md "원본과 다른 점"에 적습니다.

## 예상 분량

| 기능 | 예상 |
|---|---|
| 허브 + HTTP API | 1~1.5일 (구현 약 0.7, 테스트·비교 하네스 약 0.5) |
| 순위표 | 0.5일 |
| 비밀번호 재설정 | 0.5일 |
| Discord 연동 | 0.3~0.5일 |
| Unity 클라이언트 쪽 | 0.5일 |
| **합계** | **약 3~3.5일** (구현 1.5~2일 + 테스트) |

가장 불확실한 부분은 원본 허브와 게임 서버 여러 대를 한 가상 시계에서 결정적으로 돌리는 비교 하네스입니다.

## 원본 위치 (PixelHero-Open, `f5b6665ef`)

| 기능 | 원본 파일 |
|---|---|
| 허브 서버 | `packages/hub/src/` (약 1,540줄): `main.ts`, `network/handler.ts`·`connection.ts`(게임 서버와 WebSocket), `model/server.ts`(서버 등록, 채팅·길드·친구·접속 중계, Relay), `controllers/models.ts`(서버 목록), `controllers/api.ts`(HTTP), `controllers/cache.ts`(순위표 캐시), `controllers/mailer.ts`(메일), `model/admin.ts`, `console.ts` |
| 게임 서버 → 허브 | `packages/server/src/network/client.ts`(허브 WebSocket 클라이언트, `relay`) |
| 게임 서버 HTTP API | `packages/server/src/network/api.ts`(Express, 허브가 묻는 접속 여부·상태) |
| 순위표 집계 | `packages/common/database/mongodb/mongodb.ts`의 `aggregate` 메서드들(전체·스킬별 경험치, 몹 처치, PvP) |
| 비밀번호 재설정 | 허브 `api.ts`의 `/api/v1/requestReset`, `/api/v1/resetPassword`, `mongodb.ts`의 resetToken 처리(`player_info.resetToken`), `mailer.ts` |
| Discord | `packages/common/api/discord.ts`, 호출부: `incoming.ts`(로그인 알림), `player.ts`(로그아웃 알림, 전체 채팅), `world.ts`(`discord.onMessage` → `globalMessage`) |
| 원본 클라이언트 | `packages/client/src/network/socket.ts`(허브 `/server`로 서버 고르기), `app.ts`(`/all` 서버 목록, `requestReset`), `reset.ts`(재설정 페이지, `resetPassword`), `menu/leaderboards.ts`(순위표 창, `/leaderboards`) |

**설정 이름**(`.env.defaults`에 있음; 값은 출력하지 않음):
- 서버·허브: `API_ENABLED`, `API_PORT`, `SERVER_ID`, `ACCESS_TOKEN`, `HUB_ENABLED`, `HUB_HOST`, `HUB_WS_HOST`, `HUB_PORT`, `HUB_WS_PORT`, `REMOTE_SERVER_HOST`, `REMOTE_API_HOST`, `HUB_ACCESS_TOKEN`
- 메일: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USE_SECURE`, `SMTP_USER`, `SMTP_PASSWORD`
- Discord: `DISCORD_ENABLED`, `DISCORD_CHANNEL_ID`, `DISCORD_BOT_TOKEN`
- 순위표: `AGGREGATE_THRESHOLD`

## 지금 이식판의 자리

- C# 서버: `Server/src/PixelHero.Server.Core/Network/Hub.cs`에 `Api`, `HubClient`, `Discord`가 "꺼진 상태" 그대로 들어 있습니다. 예: `IsPlayerOnline`은 항상 false, `Send`는 아무것도 하지 않음. 여기를 실제 구현으로 바꿉니다. 호출부는 이미 원본 순서대로 이식되어 있습니다.
- DB: `IGameDatabase` → `GameDatabase` → `IDocumentStore`(메모리·MongoDB). 집계와 재설정 토큰은 이 경로에 메서드를 추가합니다. 게임 코드에는 MongoDB 타입을 드러내지 않습니다(PostgreSQL 전환 대비).
- Unity 클라이언트:
  - HUD의 순위표 버튼(`GameHud`), 프로필의 순위표 버튼(`ProfileView`)은 있지만 연결되어 있지 않습니다(`GameClient.cs` 756행 "Leaderboards (needs the hub) is not ported").
  - 첫 화면(`IntroView`)에 비밀번호 재설정 화면과 서버 목록이 있는지 확인이 필요합니다.

## 구현 단계 (1일차)

1. **허브 서버** (`Server/src/PixelHero.Hub`, 같은 솔루션의 새 실행 프로젝트, SuperSocket 2.1 + HTTP)
   - 게임 서버들의 WebSocket 접속과 인증(`HUB_ACCESS_TOKEN`), 서버 목록
   - 채팅·길드·친구·로그인/로그아웃 중계, Relay(다른 서버의 플레이어에게 패킷 전달)
   - HTTP: `/`, `/server`, `/all`, `/leaderboards`, `/isOnline`, `/api/v1/requestReset`, `/api/v1/resetPassword` 및 원본의 나머지 엔드포인트
   - 원본 허브의 구조와 이름을 따릅니다. JS 의미론이 필요하면 Core의 `Js/`를 씁니다.
2. **게임 서버 쪽**: `Hub.cs`의 `HubClient`(허브 WebSocket 클라이언트), `Api`(게임 서버 HTTP)를 원본대로 구현합니다. `HUB_ENABLED`·`API_ENABLED`가 false면 지금처럼 동작합니다.
3. **순위표**: 집계 메서드를 `IGameDatabase`/`IDocumentStore`에 추가하고 MongoDB 구현(원본 aggregate 파이프라인 그대로)과 메모리 구현을 만듭니다. 허브의 캐시(`AGGREGATE_THRESHOLD`)도 옮깁니다.
4. **비밀번호 재설정**: resetToken 생성·저장·검사(원본 mongodb.ts)와 메일 발송(SMTP)을 옮깁니다. .NET에서는 `System.Net.Mail` 또는 MailKit 중 고릅니다. 고른 이유를 PROGRESS에 남깁니다.
5. **Discord**: Discord.Net으로 원본 `discord.ts`와 같은 동작을 만듭니다. 채널의 메시지를 게임 전체 채팅으로, 게임의 접속 알림과 전체 채팅을 채널로 보냅니다.
6. **Unity 클라이언트**
   - 순위표 창: 원본 `menu/leaderboards.ts`를 이식하고, HUD·프로필 버튼을 연결합니다.
   - 서버 고르기: `socket.ts`처럼 허브의 `/server`로 서버를 받고, 허브가 없으면 지금처럼 동작합니다.
   - 비밀번호 재설정: 요청 화면(`app.ts`)과 재설정 화면(`reset.ts`). UI 문구는 ko/en 표에 함께 넣습니다(`LocalizationTests`가 검사).
   - WebGL에서는 HTTP 호출에 `UnityWebRequest`를 씁니다(브라우저 fetch와 같은 제약).
7. 빌드, 기존 서버 테스트 68개, Unity EditMode 테스트가 통과하는지 확인합니다. 하루 작업을 PROGRESS.md에 "구현 완료, 테스트 전"으로 적습니다.

## 테스트 단계 (2일차)

1. **비교 하네스 확장** (`Tools/fixtures/server/`)
   - 원본 허브와 게임 서버 2대를 한 가상 시계에서 돌립니다. 서로를 잇는 WebSocket과 HTTP는 가짜로 연결합니다.
   - 서버 간 귓속말, 전체 채팅, 친구 접속 상태, 길드 갱신, 서버 목록 시나리오를 녹화하고, C#(허브 + 서버 2대)이 바이트 단위로 같은지 봅니다.
2. **계약 테스트**: 집계 조회와 재설정 토큰을 메모리·MongoDB 두 저장소에서 같은 사례로 확인합니다(원격 `pixelhero`, `ct_` 접두어만 사용).
3. **HTTP API**: 원본 허브와 C# 허브에 같은 요청을 보내 응답을 비교합니다.
4. **사용자 확인이 필요한 것**
   - 메일: 실제 SMTP로 재설정 메일이 오는지, 링크로 비밀번호가 바뀌는지
   - Discord: 실제 채널에서 양방향 채팅과 접속 알림
   - 브라우저: 순위표 창, 비밀번호 재설정 화면, 서버 고르기
5. 일부러 버그를 심어 새 테스트가 잡는지 확인한 뒤, 통과하면 커밋합니다.

## 사용자 준비물 (2일차 전까지)

`PixelHero-Data/.env`에 넣어 주시면 됩니다. Claude는 값을 출력하지 않습니다.
- 메일: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USE_SECURE`, `SMTP_USER`, `SMTP_PASSWORD`(예: Gmail 앱 비밀번호)
- Discord: 봇을 만들어 서버에 초대한 뒤 `DISCORD_BOT_TOKEN`, `DISCORD_CHANNEL_ID`

## 주의할 점

- 기능을 꺼 둔 상태에서 기존 시나리오 38개의 녹화 결과가 바뀌면 안 됩니다. 바뀌면 "꺼진 상태" 동작이 원본과 다르다는 뜻입니다.
- 원본 서버도 같은 `PixelHero-Data/.env`를 읽습니다. 이 설정을 켜면 :9011에서 도는 원본 서버도 허브에 붙으려 합니다. 테스트 중에는 C# 쪽은 명령줄 인자(`--hubEnabled true` 등)로 켜는 편이 안전합니다.
- 포트는 쓰기 전에 `netstat -ano | grep LISTEN`으로 확인합니다. 9001, 9002, 81은 이미 다른 프로그램이 씁니다.
- MongoDB 집계는 사용자 서버 버전과 드라이버 3.9.0에서 되는지 계약 테스트로 확인합니다.
