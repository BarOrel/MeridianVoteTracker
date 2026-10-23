using MeridianVoteTracker.Application.Users.ListUsers;
using Microsoft.Extensions.DependencyInjection;

namespace MeridianVoteTracker.Application;

public static class DependencyInjection
{
    /// <summary>Registers use cases explicitly (no assembly scanning), so the composition is readable in one place.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ListUsersUseCase>();
        return services;
    }
}
