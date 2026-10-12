using System;

namespace Porchlight.Shared.Api
{
    /// <summary>HTTP routes shared by the server and every client, so they can't drift apart.</summary>
    public static class ApiRoutes
    {
        public const string Health = "/api/health";

        /// <summary>GET lists TownSummaryDto[]; POST takes a CreateTownRequest and returns a TownDto.</summary>
        public const string Towns = "/api/towns";

        /// <summary>GET returns a TownDto; PUT saves one (409 on a version conflict); DELETE removes it.</summary>
        public static string Town(string id) => Towns + "/" + Uri.EscapeDataString(id);
    }
}
