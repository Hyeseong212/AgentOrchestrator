# AgentOrchestrator

C#으로 만드는 메인 에이전트 + 동적 서브 에이전트 오케스트레이션 실험 프로젝트입니다.

## What It Does

- 메인 에이전트가 프로젝트 요청을 읽습니다.
- 태스크 플랜을 만들고 서브 에이전트 수를 자동으로 조절합니다.
- 서브 에이전트가 큐 기반으로 작업을 처리하고 재시도를 수행합니다.
- 실행 결과를 콘솔과 `runs/` 아티팩트로 남깁니다.
- 이전 실행 리포트를 다시 읽어 `history`와 `status`를 복구합니다.
- 기본 실행은 상시 실행형 메인 에이전트 호스트입니다.

## Request File

프로젝트 입력은 `AgentOrchestrator/project-request.json` 파일에서 읽습니다.

## Run

```powershell
cd AgentOrchestrator
dotnet run
```

위 명령은 인터랙티브 호스트를 실행합니다. 사용 가능한 명령:

- `run`
- `run <goal text>`
- `task <goal text>`
- `status`
- `request`
- `history`
- `clear`
- `exit`

`run <goal text>` 또는 `task <goal text>`를 쓰면 `project-request.json`을 수정하지 않고도
CLI에 직접 입력한 목표를 임시 요청으로 바꿔 바로 실행합니다.

워크스페이스 루트에서도 아래처럼 바로 실행할 수 있습니다.

```powershell
dotnet run --project AgentOrchestrator/AgentOrchestrator.csproj
```

또는 Windows에서는 루트에서 `run.cmd`를 실행해도 됩니다.

한 번만 실행하고 종료하려면:

```powershell
dotnet run --project AgentOrchestrator/AgentOrchestrator.csproj -- --once
```

## VS Code

- 지금은 `F5`를 누르면 디스코드 봇이 바로 실행됩니다.
- 빌드는 `.vscode/tasks.json`의 `build-agent-orchestrator-discord-bot` 작업을 사용합니다.
- 콘솔형 오케스트레이터를 직접 띄우고 싶으면 터미널에서 아래 명령을 사용합니다.

```powershell
dotnet run --project AgentOrchestrator/AgentOrchestrator.csproj
```

실행 후 다음 파일이 생성됩니다.

- `AgentOrchestrator/runs/<timestamp>-<project-name>/report.txt`
- `AgentOrchestrator/runs/<timestamp>-<project-name>/report.json`

## Discord Bot

디스코드에서 오케스트레이터를 직접 호출하려면 `AgentOrchestrator.DiscordBot` 프로젝트를 사용합니다.

설정 방법:

- `AgentOrchestrator.DiscordBot/appsettings.Local.json` 파일을 만들고 아래처럼 토큰을 넣습니다.
- 또는 환경변수 `DISCORD__BOTTOKEN`을 설정합니다.
- `AgentOrchestrator.DiscordBot/appsettings.json`은 저장소 기본값만 두고, 실제 토큰은 `appsettings.Local.json`에만 두는 것을 권장합니다.

```json
{
  "Discord": {
    "BotToken": "YOUR_BOT_TOKEN",
    "CommandPrefix": "!"
  }
}
```

실행:

```powershell
dotnet run --project AgentOrchestrator.DiscordBot/AgentOrchestrator.DiscordBot.csproj
```

사용 가능한 명령:

- `!ask <question>`
- `!capabilities`
- `!task <goal>`: 프로젝트 카테고리와 `main-codex`/`sub-codex-*` 채널을 만들고 실행 히스토리를 남깁니다.
- `!status`
- `!history`
- `!report <runId>`
- `!request`
- `!help`
