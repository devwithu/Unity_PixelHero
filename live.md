# 운영 서버 배포 정리 (PixelHero)

C# 허브(`Server/src/PixelHero.Hub`)와 C# 게임 서버(`Server/src/PixelHero.Server`)를 운영 서버에 올릴 때 복사할 파일, 설정, 운영 방법을 정리합니다.

## 0. 정해진 것 (2026-10-11)

| 항목 | 값 |
|---|---|
| 허브 도메인 | `hub.jdj.kr` |
| WebGL 페이지 | `pixelhero.jdj.kr` (별도 서버) |
| 배치 | 한 머신(A) 또는 나누어 두기(B), 둘 다 지원 |
| 런타임 | 운영 서버에 .NET 10 설치됨 → 프레임워크 의존 배포 |
| DB | MongoDB에 새 DB `pixelhero` (사용자 `pixelhero`) |
| 인증서 | 기존 `*.jdj.kr` 와일드카드 (Let's Encrypt) |
| 포트 | §8 표 그대로 |
| 게임 서버 이름 | `beta`(1), `gamma`(2), `delta`(3) … |
| 최대 인원 | 서버당 100명 (`MAX_PLAYERS=100`, §11) |
| 튜토리얼 / 회원가입 | 강제 / 허용 |
| 메일 / Discord | 둘 다 사용 (허브가 담당) |
| `HUB_ACCESS_TOKEN` | `.env*.sample`에 생성해 넣음 (64자 hex) |

설정 파일:

| 파일 | 쓰는 곳 |
|---|---|
| `.env.sample` | 구성 A: 허브 + 게임 서버 beta가 한 머신 |
| `.env.hub.sample` | 구성 B: 허브만 있는 머신 |
| `.env.server.sample` | 구성 B: 게임 서버만 있는 머신 (서버마다 이름·번호·도메인만 바꿈) |

- 세 파일에는 실제 `HUB_ACCESS_TOKEN`이 들어 있어 `.gitignore`에 추가했습니다. 공개 저장소에 올리지 않습니다.
- 운영 서버에서는 `/opt/pixelhero/PixelHero-Data/.env`로 복사하고 `chmod 600`합니다.

## 1. 배치 구성

### 중요한 제약: 한 머신에 게임 서버는 한 대

- WebGL 클라이언트는 https 페이지에서 게임 서버에 **항상 wss 59443**으로 붙습니다.
  - 허브가 알려 주는 것은 호스트뿐이고, wss일 때 허브가 준 포트는 쓰지 않습니다(`HubRequests.ServerUrl`).
- 그래서 게임 서버마다 **자기 도메인과 자기 59443**이 필요합니다. 한 머신에서 beta와 gamma를 함께 띄울 수 없습니다.
- 한 머신에 여러 대를 두고 싶으면 클라이언트 수정이 필요합니다(예: 허브가 각 서버의 `SSL_PORT`도 알려 주게 하기). 필요하면 말씀해 주세요.

### 구성 A: 한 머신 (허브 + beta)

```
브라우저 ──https──> pixelhero.jdj.kr (WebGL 페이지, 별도 서버)
   ├──https 59536──> hub.jdj.kr  [허브]
   └──wss   59443──> hub.jdj.kr  [게임 서버 beta]
                      허브 ↔ beta: 127.0.0.1:59526/59527 (머신 안)
```

- 설정: `.env.sample` 하나.
- 나중에 gamma를 늘리면 다른 머신에 구성 B의 게임 서버로 추가합니다(A + B 혼합).
  - 그때 이 머신의 59526·59527을 gamma 머신 IP에만 엽니다.

### 구성 B: 나누어 두기

```
브라우저 ──https──> pixelhero.jdj.kr (WebGL 페이지)
   ├──https 59536──> hub.jdj.kr   [허브 머신]
   ├──wss   59443──> beta.jdj.kr   [게임 서버 beta]
   ├──wss   59443──> gamma.jdj.kr   [게임 서버 gamma]
   └──wss   59443──> delta.jdj.kr   [게임 서버 delta]
                      게임 서버들 ──http 59526 / ws 59527──> 허브 머신
```

- 허브 머신은 `.env.hub.sample`, 게임 서버 머신은 `.env.server.sample`을 씁니다.
- 게임 서버마다 바꿀 값:

| 서버 | `NAME` | `SERVER_ID` | `REMOTE_SERVER_HOST` |
|---|---|---|---|
| beta | `beta` | 1 | `beta.jdj.kr` |
| gamma | `gamma` | 2 | `gamma.jdj.kr` |
| delta | `delta` | 3 | `delta.jdj.kr` |

- 도메인은 `*.jdj.kr` 한 단계 아래여야 와일드카드 인증서가 맞습니다.
  - `beta.jdj.kr`은 맞습니다.
  - `beta.pixelhero.jdj.kr`은 두 단계 아래라서 맞지 않습니다.
- DNS A 레코드를 각 머신의 공인 IP로 지정합니다.
- 허브 연결(59526 http, 59527 ws)은 **암호화되지 않습니다.**
  - 머신끼리 사설망이 있으면 `HUB_HOST`/`HUB_WS_HOST`에 허브의 사설 IP를 씁니다.
  - 사설망이 없으면 허브 머신 방화벽에서 게임 서버 IP만 허용합니다.

## 2. 두 프로그램이 파일을 찾는 방식

- 실행 파일은 **현재 디렉터리 또는 실행 파일 폴더**에서 위로 올라가며 `PixelHero-Data/server/data` 폴더가 있는 곳을 찾습니다. 그곳이 "루트"입니다(`Program.cs`의 `FindRepositoryRoot`).
- 설정은 `<루트>/PixelHero-Data/.env.defaults`를 읽고, `<루트>/PixelHero-Data/.env`로 덮어쓴 뒤, 명령줄 `--key value`로 다시 덮어씁니다.
- 인증서 경로가 상대 경로면 루트 기준입니다. 아래 예의 루트는 `/opt/pixelhero`입니다.

```
/opt/pixelhero/                                  ← 루트
├── bin/
│   ├── hub/                                     ← 허브 빌드 결과 (허브 머신)
│   └── server/                                  ← 게임 서버 빌드 결과 (게임 서버 머신)
├── certs/fullchain.pem, privkey.pem             ← *.jdj.kr 인증서 (§5, 원본은 개발 PC의 PixelHero-Data/server/certs/)
├── PixelHero-Data/
│   ├── .env.defaults                            ← 그대로 복사
│   ├── .env                                     ← .env*.sample을 채워서 만듦 (chmod 600)
│   ├── common/text/profanity.json               ← 게임 서버
│   └── server/data/                             ← 허브는 mobs.json만, 게임 서버는 전부
└── Assets/PixelHero/Localization/server.en.json   ← 게임 서버
```

## 3. 빌드와 복사 (개발 PC에서)

운영 서버에 .NET 10이 있으므로 프레임워크 의존 배포를 씁니다.

- 두 프로그램 모두 Kestrel을 쓰므로 운영 서버에 **ASP.NET Core Runtime 10**도 있어야 합니다.
- `dotnet --list-runtimes`에 `Microsoft.AspNetCore.App 10.x`가 보이는지 확인합니다. 없으면 운영 서버에서 설치합니다.
  ```bash
  sudo apt update
  sudo apt install -y aspnetcore-runtime-10.0
  sudo apt install -y dotnet-runtime-10.0
  ```

```bash
dotnet publish Server/src/PixelHero.Server -c Release -o publish/server
dotnet publish Server/src/PixelHero.Hub    -c Release -o publish/hub
```

- 아래 것들은 빌드할 때 DLL 안에 들어가므로 따로 복사하지 않습니다.
  - `map_fixes.json`(지도 보정, EmbeddedResource)
  - Unity와 공유하는 코드

### 허브 머신에 복사할 것

| 운영 서버 경로 | 개발 PC 원본 | 비고 |
|---|---|---|
| `bin/hub/` | `publish/hub/` 전체 | |
| `PixelHero-Data/.env.defaults` | 같은 경로 | |
| `PixelHero-Data/.env` | `.env.sample`(A) 또는 `.env.hub.sample`(B) | `<...>`를 채움 |
| `PixelHero-Data/server/data/mobs.json` | 같은 경로 | 순위표용. 이 폴더가 있어야 루트를 찾음 |
| `certs/` | `PixelHero-Data/server/certs/`의 `fullchain.pem`, `privkey.pem` | 허브 https 59536 |

### 게임 서버 머신에 복사할 것

| 운영 서버 경로 | 개발 PC 원본 | 비고 |
|---|---|---|
| `bin/server/` | `publish/server/` 전체 | |
| `PixelHero-Data/.env.defaults` | 같은 경로 | |
| `PixelHero-Data/.env` | `.env.sample`(A) 또는 `.env.server.sample`(B) | B는 서버마다 §1 표의 값 |
| `PixelHero-Data/server/data/*.json` | 같은 경로 | 최상위 14개, 하나라도 없으면 시작 실패 |
| `PixelHero-Data/server/data/map/world.json` | 같은 경로 | **지도(약 4.1MB)**. 리전 데이터는 시작할 때 만듦 |
| `PixelHero-Data/server/data/quests/*.json` | 같은 경로 | 퀘스트 21개 |
| `PixelHero-Data/server/data/crafting/*.json` | 같은 경로 | 제작 7종 |
| `PixelHero-Data/common/text/profanity.json` | 같은 경로 | 채팅 필터. **없으면 시작 실패** |
| `Assets/PixelHero/Localization/server.en.json` | 같은 경로 | 서버 메시지 표. 없으면 메시지가 키 그대로 나감 |
| `certs/` | `PixelHero-Data/server/certs/`의 `fullchain.pem`, `privkey.pem` | wss 59443 |

- 최상위 JSON 14개는 다음과 같습니다.
  - `abilities`, `achievements`, `effectentities`, `fishing`, `foraging`, `items`, `minigames`
  - `mobs`, `npcs`, `rocks`, `spawns`, `stores`, `tables`, `trees`
- 다음은 복사하지 않습니다.
  - 소스, `Tools/`, `Docs/`, Unity 프로젝트의 나머지

### 복사 예 (rsync, 저장소 루트에서)

```bash
# 공통 데이터 (허브 머신에도 그대로 보내도 됨: 허브는 mobs.json만 읽음)
DATA="PixelHero-Data/.env.defaults \
  PixelHero-Data/common/text/profanity.json \
  PixelHero-Data/server/data/*.json \
  PixelHero-Data/server/data/map/world.json \
  PixelHero-Data/server/data/quests \
  PixelHero-Data/server/data/crafting/*.json \
  Assets/PixelHero/Localization/server.en.json"

# 구성 A
rsync -avR $DATA                user@hub.jdj.kr:/opt/pixelhero/
rsync -av publish/hub/          user@hub.jdj.kr:/opt/pixelhero/bin/hub/
rsync -av publish/server/       user@hub.jdj.kr:/opt/pixelhero/bin/server/

# 구성 B: 허브 머신은 bin/hub, 게임 서버 머신마다 bin/server
rsync -avR $DATA                user@hub.jdj.kr:/opt/pixelhero/
rsync -av publish/hub/          user@hub.jdj.kr:/opt/pixelhero/bin/hub/
for host in beta.jdj.kr gamma.jdj.kr delta.jdj.kr; do
  rsync -avR $DATA              user@$host:/opt/pixelhero/
  rsync -av publish/server/     user@$host:/opt/pixelhero/bin/server/
done


rsync -avR -e 'ssh -p 60079' \
  PixelHero-Data/.env.defaults \
  PixelHero-Data/common/text/profanity.json \
  PixelHero-Data/server/data/ \
  PixelHero-Data/server/data/map/world.json \
  PixelHero-Data/server/data/quests \
  PixelHero-Data/server/data/crafting/ \
  Assets/PixelHero/Localization \
  ubuntu@base2.jdj.kr:/opt/pixelhero/
rsync -av -e 'ssh -p 60079' publish/hub/          ubuntu@base2.jdj.kr:/opt/pixelhero/bin/hub/

for host in beta.ogs.kr gamma.ogs.kr delta.ogs.kr; do
  rsync -avR \
    PixelHero-Data/.env.defaults \
    PixelHero-Data/common/text/profanity.json \
    PixelHero-Data/server/data/ \
    PixelHero-Data/server/data/map/world.json \
    PixelHero-Data/server/data/quests \
    PixelHero-Data/server/data/crafting/ \
    Assets/PixelHero/Localization \
    ubuntu@$host:/opt/pixelhero/ 
  rsync -av publish/server/     ubuntu@$host:/opt/pixelhero/bin/server/
done
```

- 업데이트할 때는 서버를 멈추고 `bin/`과 데이터를 덮어쓴 뒤 다시 시작합니다.
- 허브와 게임 서버는 `GVER`가 같아야 등록됩니다.

## 4. 실행 확인

```bash
cd /opt/pixelhero/bin/hub    && dotnet PixelHero.Hub.dll
cd /opt/pixelhero/bin/server && dotnet PixelHero.Server.dll
```

- **허브 로그**
  - "Successfully connected to the MongoDB server."
  - "Successfully connected to the Discord server."
  - "Hub https listener on port 59536 (jdj.kr, …)"
- **게임 서버 로그**
  - "Region cache has been successfully created."
  - "Secure WebSocket (wss) listener on port: 59443 (…)"
- **허브 로그(게임 서버 등록 후)**: "beta 1 has come online!"
- **확인 명령**: `curl https://hub.jdj.kr:59536/all`
  - 등록된 서버 목록이 나와야 합니다.
  - `/server`가 `{"status":"error"}`면 등록된 서버가 없거나 모두 가득 찬 것입니다.
- **로그에 "listener is off"가 보이면** 인증서를 읽지 못한 것입니다. 그 경우 게임 서버는 ws만, 허브는 http만 엽니다.
- **게임 서버를 끌 때**: SIGINT(Ctrl+C)를 받으면 모든 플레이어를 저장한 뒤 끝납니다. 그래서 서비스도 SIGINT로 끄게 합니다(§7).

## 5. SSL 인증서 (*.jdj.kr)

- 개발 PC의 인증서는 저장소의 `PixelHero-Data/server/certs/`에 있습니다. 이 폴더는 `.gitignore`에 들어 있어 커밋되지 않습니다.

| 파일 | 내용 | 운영 배포 |
|---|---|---|
| `fullchain.pem` | `*.jdj.kr`, `jdj.kr` 와일드카드 (Let's Encrypt, **2026-11-16 만료**) | 복사함 |
| `privkey.pem` | 위 인증서의 개인 키 | 복사함 |
| `localhost.pfx` | 로컬 개발용 (localhost wss) | 복사하지 않음 |
| `test-letsencrypt/` | 시험용 | 복사하지 않음 |

- `fullchain.pem`과 `privkey.pem`을 각 머신의 `/opt/pixelhero/certs/`에 복사합니다.
  - 허브 머신과 게임 서버 머신(beta, gamma, delta) 모두 필요합니다.
  - `.env`의 `SSL_CERTIFICATE`와 `SSL_CERTIFICATE_KEY`가 이 경로를 가리킵니다(`.env*.sample`에 이미 들어 있음).
  - 권한: `chown pixelhero: /opt/pixelhero/certs/*`, `chmod 600 /opt/pixelhero/certs/privkey.pem`
  ```bash
  # 저장소 루트에서
  for host in hub.jdj.kr beta.jdj.kr gamma.jdj.kr delta.jdj.kr; do
    rsync -av PixelHero-Data/server/certs/fullchain.pem PixelHero-Data/server/certs/privkey.pem user@$host:/opt/pixelhero/certs/
  done
  ```
  - 운영 서버에서는 `PixelHero-Data/server/` 아래가 아니라 `/opt/pixelhero/certs/`에 둡니다. 데이터 폴더(`server/data`)와 섞이지 않게 하려는 것입니다.
- 만료일 확인: `openssl x509 -in PixelHero-Data/server/certs/fullchain.pem -noout -enddate`
- WebGL 페이지 서버(`pixelhero.jdj.kr`)도 같은 인증서를 씁니다.
- **갱신**: 와일드카드는 DNS-01 인증이라 갱신을 한 곳(인증서를 받은 PC나 서버)에서 합니다.
  - 갱신한 뒤 개발 PC의 `PixelHero-Data/server/certs/`도 새 파일로 바꿉니다.
  - 모든 머신에 다시 배포하고, 허브와 게임 서버를 재시작합니다. 둘 다 인증서를 시작할 때 한 번만 읽습니다.
  - certbot이라면 deploy-hook으로 자동화합니다.
  ```sh
  # /etc/letsencrypt/renewal-hooks/deploy/pixelhero.sh  (인증서를 받는 머신, chmod +x)
  #!/bin/sh
  for host in hub.jdj.kr beta.jdj.kr gamma.jdj.kr delta.jdj.kr; do
    scp "$RENEWED_LINEAGE/fullchain.pem" "$RENEWED_LINEAGE/privkey.pem" deploy@$host:/opt/pixelhero/certs/
    ssh deploy@$host 'sudo systemctl restart pixelhero-hub pixelhero-server 2>/dev/null; true'
  done
  ```
  - 만료 2주 전까지 갱신되지 않았으면 직접 확인합니다(§11 모니터링).

## 6. MongoDB `pixelhero` 준비

```js
// mongosh, 관리자 계정으로
use pixelhero
db.createUser({ user: "pixelhero", pwd: "<비밀번호>", roles: [{ role: "readWrite", db: "pixelhero" }] })
```

- 사용자를 `pixelhero` DB에 만들었으므로 `MONGODB_AUTH_SOURCE='pixelhero'`입니다.
- 컬렉션(`player_info` 등)은 처음 저장할 때 생깁니다.
- MongoDB 서버 방화벽에서 허브 머신과 게임 서버 머신의 IP를 허용합니다.
- MongoDB.Driver는 3.9.0에 고정되어 있습니다. 3.10 이상은 지금 MongoDB 서버 버전을 거부합니다.
- **관리자 계정**
  1. 게임에서 가입합니다.
  2. 그다음 등급을 줍니다.
     ```js
     use pixelhero
     db.player_info.updateOne({ username: "<아이디>" }, { $set: { rank: 2 } })
     ```
  3. 접속 중이면 다시 로그인해야 반영됩니다.

## 7. 서비스 등록 (systemd)

서비스 계정을 만듭니다: `sudo useradd -r -s /usr/sbin/nologin -d /opt/pixelhero pixelhero`

```ini
# /etc/systemd/system/pixelhero-hub.service  (허브 머신)
[Unit]
Description=PixelHero hub
After=network-online.target
Wants=network-online.target

[Service]
User=pixelhero
WorkingDirectory=/opt/pixelhero/bin/hub
ExecStart=/usr/bin/dotnet /opt/pixelhero/bin/hub/PixelHero.Hub.dll
KillSignal=SIGINT
TimeoutStopSec=30
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```ini
# /etc/systemd/system/pixelhero-server.service  (게임 서버 머신)
[Unit]
Description=PixelHero game server
After=network-online.target
Wants=network-online.target

[Service]
User=pixelhero
WorkingDirectory=/opt/pixelhero/bin/server
ExecStart=/usr/bin/dotnet /opt/pixelhero/bin/server/PixelHero.Server.dll
KillSignal=SIGINT
TimeoutStopSec=60
# 재시작 직후의 재접속 거부(5초)와 겹치지 않게
RestartSec=10
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now pixelhero-hub      # 허브 머신
sudo systemctl enable --now pixelhero-server   # 게임 서버 머신
journalctl -u pixelhero-server -f              # 로그 보기
```

- 구성 A는 한 머신에 두 서비스를 모두 등록합니다.
- 허브가 늦게 떠도 게임 서버는 5초마다 다시 등록을 시도하므로 시작 순서는 상관없습니다.
- `dotnet` 경로는 `which dotnet`으로 확인합니다.

## 8. 포트와 방화벽

| 포트 | 머신 | 프로그램 | 공개 범위 |
|---|---|---|---|
| 443 | 페이지 서버 | nginx (WebGL) | 외부 |
| 59536 | 허브 | 허브 https API | 외부 |
| 59443 | 게임 서버 | wss | 외부 |
| 59526 | 허브 | 허브 http API (`/isOnline`) | 게임 서버 IP만 (A는 닫음) |
| 59527 | 허브 | 허브 ws (등록·중계) | 게임 서버 IP만 (A는 닫음) |
| 59011 | 게임 서버 | ws | 닫음 |
| 59002 | 게임 서버 | 서버 정보 API | 닫음 |
| 27017 | MongoDB | DB | 허브·게임 서버 IP만 |

```bash
# 예: ufw, 구성 B의 허브 머신 (게임 서버 IP가 1.2.3.4일 때)
sudo ufw allow 59536/tcp
sudo ufw allow from 1.2.3.4 to any port 59526,59527 proto tcp
# 게임 서버 머신
sudo ufw allow 59443/tcp
```

## 9. WebGL 페이지 (pixelhero.jdj.kr, 별도 서버)

- `Builds/WebGL` 폴더 전체를 예를 들어 `/var/www/pixelhero`에 복사합니다.
- 페이지 주소에 `?hub=`가 없으면 클라이언트가 **페이지 호스트**(`pixelhero.jdj.kr:59443`)로 붙으려 해서 실패합니다. 그래서 `?hub=`가 없을 때 붙여 주도록 리디렉트합니다.
  - 재설정 메일 링크(`RESET_URL`)에는 `?hub=`가 이미 들어 있습니다.
- 빌드는 gzip 압축이고 압축 해제 대비 기능이 켜져 있습니다. 그래서 헤더가 없어도 동작하지만, 아래 헤더를 주면 로딩이 빠릅니다.
- 빌드 파일(`Build/`)은 이름이 내용 해시라서 빌드마다 바뀝니다. 그래서 `index.html`과 `ServiceWorker.js`는 매번 서버에 확인하게(`no-cache`) 하고, `Build/`는 오래 캐시합니다. `index.html`이 캐시되면 새로 배포해도 옛 빌드를 계속 불러옵니다.
- PWA 형식은 `Assets/WebGLTemplates/PixelHeroPWA` 템플릿입니다. Unity 기본 PWA 템플릿의 서비스 워커는 모든 응답을 캐시 우선으로 돌려줘서 새 배포가 반영되지 않습니다. 그래서 서비스 워커를 이렇게 바꿨습니다.
  - 캐시 이름이 빌드마다 다르고(해시 파일 이름), 새 서비스 워커는 바로 활성화되어 옛 캐시를 지웁니다.
  - 페이지(`index.html`)는 네트워크 우선이고, 오프라인일 때만 캐시를 씁니다. `Build/` 파일은 캐시 우선입니다.
  - 같은 출처의 GET만 캐시합니다. 허브 HTTP API(랭킹 등)는 캐시하지 않습니다.
  - 새 버전은 배포 후 **페이지를 새로 열 때** 적용됩니다. 게임 중인 탭은 그대로 둡니다.

```nginx
server {
    listen 443 ssl http2;
    server_name pixelhero.jdj.kr;
    ssl_certificate     /etc/ssl/jdj.kr/fullchain.pem;
    ssl_certificate_key /etc/ssl/jdj.kr/privkey.pem;
    root /var/www/pixelhero;

    location = / {
        if ($arg_hub = "") { return 302 /?hub=https://hub.jdj.kr:59536; }
        add_header Cache-Control "no-cache";
        try_files /index.html =404;
    }

    location = /index.html       { add_header Cache-Control "no-cache"; }
    location = /ServiceWorker.js { add_header Cache-Control "no-cache, no-store"; }
    location /Build/             { add_header Cache-Control "public, max-age=31536000, immutable"; }

    location ~ \.wasm\.gz$ { gzip off; add_header Content-Encoding gzip; default_type application/wasm; }
    location ~ \.js\.gz$   { gzip off; add_header Content-Encoding gzip; default_type application/javascript; }
    location ~ \.data\.gz$ { gzip off; add_header Content-Encoding gzip; default_type application/octet-stream; }
}

server {
    listen 80;
    server_name pixelhero.jdj.kr;
    return 301 https://$host$request_uri;
}
```

```bash
rsync -avR -e 'ssh -p 60079' \
  Build TemplateData \
  index.html manifest.webmanifest ServiceWorker.js \
  ubuntu@base2.jdj.kr:~/pixelhero/
```

- 해시 이름이라 서버의 `Build/`에 옛 빌드 파일이 쌓입니다. 가끔 `Build/`에서 지금 `index.html`이 가리키지 않는 파일을 지웁니다.

- 확인 주소: `https://pixelhero.jdj.kr/` → `?hub=…`가 붙은 주소로 바뀌고 월드 선택에 beta가 나오면 됩니다.
- 허브는 cors()로 모든 출처를 허용하므로 다른 도메인의 페이지에서도 호출됩니다.

## 10. Discord와 메일

- **Discord**: 허브를 켜면 게임 서버는 Discord에 접속하지 않고 허브가 맡습니다.
  - 설정은 허브의 `.env`에만 넣습니다.
  - Developer Portal → Bot → Privileged Gateway Intents에서 **Presence Intent**와 **Message Content Intent**를 켭니다.
  - 봇에 채널 보기, 메시지 보내기, 기록 읽기, 채널 관리(주제 변경) 권한을 줍니다.
  - ``DISCORD_CHANNEL_ID=`…` ``처럼 백틱으로 감쌉니다.
- **메일**: Gmail 앱 비밀번호(16자)를 씁니다. `SMTP_USER`가 비어 있으면 보내지 않습니다.
  - 운영용 발신 계정을 따로 만드는 것을 권합니다.
  - Gmail은 하루 발송 한도가 있습니다(일반 계정 약 500통).

## 11. 운영 관리 (추천)

- **최대 인원 100명**
  - 한 서버의 월드는 스레드 하나에서 돕니다. 원본 기본값은 200명이지만, C# 서버에 부하 시험을 아직 하지 않았습니다.
  - 그래서 100명으로 시작합니다. 동시 접속이 늘면 서버(gamma, delta)를 추가하고, CPU에 여유가 있으면 올립니다.
- **로그**
  - 두 프로그램은 표준 출력에만 씁니다. systemd가 journald에 모아 줍니다.
  - `/etc/systemd/journald.conf`에서 `SystemMaxUse=1G`, `MaxRetentionSec=30day` 정도로 보관합니다.
  - 평소에는 `DEBUGGING=false`로 둡니다.
- **백업**
  - 매일 새벽 `mongodump`를 MongoDB 서버 밖(다른 머신이나 저장소)으로 보내고, 14일분을 보관합니다.
    ```bash
    # crontab (pixelhero DB 사용자에게는 backup 권한이 필요하거나 관리자 계정을 씀)
    0 4 * * * mongodump --uri "mongodb://<user>:<pw>@<host>:27017/pixelhero?authSource=pixelhero" --gzip --archive=/backup/pixelhero-$(date +\%F).gz && find /backup -name 'pixelhero-*.gz' -mtime +14 -delete
    ```
  - 한 달에 한 번은 복원 시험을 합니다(시험용 DB에 `mongorestore`).
  - 게임 서버는 60초마다(`SAVE_INTERVAL`), 그리고 SIGINT로 끌 때 저장합니다. 그래서 `kill -9`나 강제 종료는 피합니다.
- **모니터링**
  - 외부에서 5분마다 `https://hub.jdj.kr:59536/all`을 확인합니다(UptimeRobot 등 무료 서비스).
  - 서버가 빠지면 알림을 받습니다.
  - 인증서 만료일도 같은 서비스로 감시합니다.
- **업데이트 절차**
  1. Discord나 공지로 미리 알립니다.
  2. `systemctl stop pixelhero-server`로 끕니다. 끌 때 저장합니다.
  3. `bin/`과 데이터를 교체하고 시작합니다.
  4. 허브는 게임 서버가 다시 붙을 때까지 서버 목록에서 그 서버를 뺍니다.
- **보안**
  - `.env`는 `chmod 600`으로 둡니다.
  - 서비스 계정에는 로그인 셸을 주지 않습니다.
  - `OVERWRITE_AUTH=false`와 `SKIP_DATABASE=false`인지 배포할 때마다 확인합니다.
  - `HUB_ACCESS_TOKEN`이 새어 나가면 새로 만들어 모든 머신에 바꿉니다.
