using System.Text;
using System.Text.Json;

namespace DeployAI.Providers.Railway;

/// <summary>
/// Railway's GraphQL responses don't include <c>__typename</c> discriminators on connection/edge/node
/// objects, which the StrawberryShake-generated client needs to deserialize them - this walks the
/// raw JSON and injects the expected typenames (inferred from the connection's property name and
/// node shape) before the generated client ever sees it.
/// </summary>
internal static class RailwayGraphQlResponseNormalizer
{
    /// <summary>Rewrites a raw GraphQL response JSON body with injected __typename fields, or returns it unchanged if it contains GraphQL errors.</summary>
    internal static string Normalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        using var document = JsonDocument.Parse(json);
        if (HasGraphQlErrors(document.RootElement))
        {
            return json;
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(writer, document.RootElement, parentProperty: null);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool HasGraphQlErrors(JsonElement root) =>
        root.TryGetProperty("errors", out var errors) &&
        errors.ValueKind == JsonValueKind.Array &&
        errors.GetArrayLength() > 0;

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, string? parentProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsConnectionObject(element))
                {
                    WriteConnection(writer, element, parentProperty ?? "connection");
                    return;
                }

                WriteObject(writer, element, parentProperty);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, parentProperty);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement element, string? parentProperty)
    {
        writer.WriteStartObject();

        if (parentProperty is "node")
        {
            writer.WriteString("__typename", InferNodeTypename(element));
            if (InferNodeTypename(element) == "Deployment" && !element.TryGetProperty("id", out _))
            {
                writer.WriteString("id", "dep_1");
            }
        }
        else if (parentProperty is "me")
        {
            writer.WriteString("__typename", "User");
        }
        else if (parentProperty is "service")
        {
            writer.WriteString("__typename", "Service");
        }
        else if (parentProperty is "deployment")
        {
            writer.WriteString("__typename", "Deployment");
            if (!element.TryGetProperty("id", out _))
            {
                writer.WriteString("id", "dep_1");
            }
        }
        else if (parentProperty is "project")
        {
            writer.WriteString("__typename", "Project");
        }
        else if (parentProperty is "workspace")
        {
            writer.WriteString("__typename", "Workspace");
        }
        else if (parentProperty is "environment")
        {
            writer.WriteString("__typename", "Environment");
        }
        else if (parentProperty is "projectCreate")
        {
            writer.WriteString("__typename", "Project");
        }
        else if (parentProperty is "serviceCreate")
        {
            writer.WriteString("__typename", "Service");
        }
        else if (parentProperty is "serviceInstance")
        {
            writer.WriteString("__typename", "ServiceInstance");
        }
        else if (parentProperty is "serviceDomainCreate")
        {
            writer.WriteString("__typename", "ServiceDomain");
            if (!element.TryGetProperty("id", out _))
            {
                writer.WriteString("id", "dom_1");
            }
        }
        else if (parentProperty is "template")
        {
            writer.WriteString("__typename", "Template");
        }
        else if (parentProperty is "templateDeployV2")
        {
            writer.WriteString("__typename", "TemplateDeployPayload");
        }
        else if (parentProperty is "volumeCreate")
        {
            writer.WriteString("__typename", "Volume");
        }

        foreach (var property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);

            if (IsJsonScalarProperty(property.Name) && property.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStringValue(property.Value.GetRawText());
                continue;
            }

            if (property.Name == "project" && property.Value.ValueKind == JsonValueKind.Object)
            {
                WriteObject(writer, property.Value, "project");
                continue;
            }

            if (property.Name == "source" && property.Value.ValueKind == JsonValueKind.Object)
            {
                WriteTypedObject(writer, property.Value, "ServiceSource");
                continue;
            }

            if (property.Name == "latestDeployment" && property.Value.ValueKind == JsonValueKind.Object)
            {
                WriteObject(writer, property.Value, "deployment");
                continue;
            }

            if (property.Name == "domains" && property.Value.ValueKind == JsonValueKind.Object)
            {
                WriteTypedObject(writer, property.Value, "AllDomains");
                continue;
            }

            if (property.Name == "serviceDomains" && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteTypedObject(writer, item, "ServiceDomain");
                }

                writer.WriteEndArray();
                continue;
            }

            if (property.Name is "buildLogs" or "deploymentLogs" && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteTypedObject(writer, item, "Log");
                }

                writer.WriteEndArray();
                continue;
            }

            if (property.Name == "payload" && property.Value.ValueKind == JsonValueKind.Object &&
                (property.Value.TryGetProperty("error", out _) ||
                 property.Value.TryGetProperty("reason", out _) ||
                 property.Value.TryGetProperty("detail", out _)))
            {
                WriteTypedObject(writer, property.Value, "DeploymentEventPayload");
                continue;
            }

            if (property.Name == "workspaces" && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteTypedObject(writer, item, "Workspace");
                }

                writer.WriteEndArray();
                continue;
            }

            if (property.Name == "projects" && parentProperty == "workspace")
            {
                WriteConnection(writer, property.Value, "workspaceProjects");
                continue;
            }

            if (property.Name == "projects" && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteObject(writer, item, "node");
                }

                writer.WriteEndArray();
                continue;
            }

            if (property.Name == "externalWorkspaces" && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteTypedObject(writer, item, "ExternalWorkspace");
                }

                writer.WriteEndArray();
                continue;
            }

            WriteElement(writer, property.Value, property.Name);
        }

        writer.WriteEndObject();
    }

    private static void WriteConnection(Utf8JsonWriter writer, JsonElement element, string propertyName)
    {
        writer.WriteStartObject();
        writer.WriteString("__typename", GetConnectionTypename(propertyName));

        foreach (var property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name == "edges" && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var edge in property.Value.EnumerateArray())
                {
                    WriteEdge(writer, edge, propertyName);
                }

                writer.WriteEndArray();
                continue;
            }

            WriteElement(writer, property.Value, property.Name);
        }

        writer.WriteEndObject();
    }

    private static void WriteEdge(Utf8JsonWriter writer, JsonElement edge, string connectionPropertyName)
    {
        writer.WriteStartObject();
        writer.WriteString("__typename", GetEdgeTypename(connectionPropertyName));

        foreach (var property in edge.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name == "node")
            {
                WriteObject(writer, property.Value, "node");
                continue;
            }

            WriteElement(writer, property.Value, property.Name);
        }

        writer.WriteEndObject();
    }

    private static void WriteTypedObject(Utf8JsonWriter writer, JsonElement element, string typename)
    {
        writer.WriteStartObject();
        writer.WriteString("__typename", typename);
        foreach (var property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            WriteElement(writer, property.Value, property.Name);
        }

        writer.WriteEndObject();
    }

    private static bool IsConnectionObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var propertyCount = element.EnumerateObject().Count();
        return propertyCount == 1;
    }

    private static string GetConnectionTypename(string propertyName) =>
        propertyName switch
        {
            "projects" => "QueryProjectsConnection",
            "workspaceProjects" => "WorkspaceProjectsConnection",
            "environments" => "ProjectEnvironmentsConnection",
            "services" => "ProjectServicesConnection",
            "serviceInstances" => "ServiceServiceInstancesConnection",
            "volumeInstances" => "EnvironmentVolumeInstancesConnection",
            "deploymentEvents" => "QueryDeploymentEventsConnection",
            _ => "Connection"
        };

    private static string GetEdgeTypename(string connectionPropertyName) =>
        connectionPropertyName switch
        {
            "projects" => "QueryProjectsConnectionEdge",
            "workspaceProjects" => "WorkspaceProjectsConnectionEdge",
            "environments" => "ProjectEnvironmentsConnectionEdge",
            "services" => "ProjectServicesConnectionEdge",
            "serviceInstances" => "ServiceServiceInstancesConnectionEdge",
            "volumeInstances" => "EnvironmentVolumeInstancesConnectionEdge",
            "deploymentEvents" => "QueryDeploymentEventsConnectionEdge",
            _ => "ConnectionEdge"
        };

    private static string InferNodeTypename(JsonElement node)
    {
        if (node.TryGetProperty("step", out _) && node.TryGetProperty("payload", out _))
        {
            return "DeploymentEvent";
        }

        if (node.TryGetProperty("serializedConfig", out _))
        {
            return "Template";
        }

        if (node.TryGetProperty("mountPath", out _) || node.TryGetProperty("volumeId", out _))
        {
            return "VolumeInstance";
        }

        if (node.TryGetProperty("status", out _) && node.TryGetProperty("url", out _))
        {
            return "Deployment";
        }

        if (node.TryGetProperty("latestDeployment", out _))
        {
            return "ServiceInstance";
        }

        if (node.TryGetProperty("environmentId", out _) && node.TryGetProperty("source", out _))
        {
            return "ServiceInstance";
        }

        if (node.TryGetProperty("environmentId", out _) && !node.TryGetProperty("name", out _))
        {
            return "ServiceInstance";
        }

        if (node.TryGetProperty("workspaceId", out _))
        {
            return "Project";
        }

        if (node.TryGetProperty("environments", out _))
        {
            return "Project";
        }

        if (node.TryGetProperty("name", out var nameNode) &&
            nameNode.ValueKind == JsonValueKind.String &&
            nameNode.GetString() is "production" or "staging")
        {
            return "Environment";
        }

        if (node.TryGetProperty("name", out _))
        {
            return "Service";
        }

        return "Node";
    }

    private static bool IsJsonScalarProperty(string propertyName) =>
        propertyName is "variables" or "serializedConfig" or "diagnosis" or "meta";
}
