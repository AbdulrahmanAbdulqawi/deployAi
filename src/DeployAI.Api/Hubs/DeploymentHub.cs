using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeployAI.Api.Hubs;

[Authorize]
public sealed class DeploymentHub : Hub
{
    public Task JoinDeployment(string deploymentId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, deploymentId);
    }

    public Task LeaveDeployment(string deploymentId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, deploymentId);
    }
}
