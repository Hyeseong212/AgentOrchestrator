using AgentOrchestrator.Agents;
using AgentOrchestrator.Models;
using AgentOrchestrator.Services;

var projectRequest = new ProjectRequest(
    Name: "Agentic Delivery Platform",
    Goal: "메인 에이전트가 작업을 분해하고 서브 에이전트가 동적으로 늘어나며 결과를 집계하는 MVP를 만든다.",
    Deliverables:
    [
        "코어 오케스트레이션 루프",
        "동적 서브 에이전트 스케일링",
        "작업 결과 집계와 최종 보고",
        "다음 확장 포인트 정의"
    ]);

var planner = new TaskPlanner();
var scaler = new AgentScaler(minAgents: 1, maxAgents: 6, tasksPerAgent: 2);
var subAgentFactory = new SubAgentFactory();
var manager = new AgentManager(planner, scaler, subAgentFactory);
var mainAgent = new MainAgent(manager);

ExecutionReport report = await mainAgent.RunProjectAsync(projectRequest);

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine(report.ToConsoleText());
