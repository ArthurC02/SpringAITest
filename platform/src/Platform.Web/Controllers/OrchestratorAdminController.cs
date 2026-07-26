using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;

namespace Platform.Web.Controllers;

/// <summary>Orchestrator 管理端點與 workflows 的動作集逐字相同,全部繼承自
/// <see cref="WorkflowAdminControllerBase"/>;此處只綁路由與 backend resource 名稱。</summary>
[Route("api/admin/orchestrators")]
public sealed class OrchestratorAdminController : WorkflowAdminControllerBase
{
    public OrchestratorAdminController(IWorkflowAdminService service) : base(service, "orchestrators")
    {
    }
}
