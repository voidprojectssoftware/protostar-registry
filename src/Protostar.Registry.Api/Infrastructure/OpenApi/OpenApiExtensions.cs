using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Protostar.Registry.Api.Infrastructure;

/// <summary>One OAuth or OpenID Connect protocol parameter, documented as a query parameter or a form field.</summary>
/// <param name="Name">The protocol's parameter name, for example <c>client_id</c>.</param>
/// <param name="Required">Whether the server rejects a request without it.</param>
/// <param name="Description">What the parameter means, in Markdown.</param>
/// <param name="Allowed">The only accepted values, when the parameter is an enumeration.</param>
public sealed record ProtocolParameter(string Name, bool Required, string Description, params string[] Allowed);

/// <summary>Endpoint builders that describe protocol endpoints whose requests are read by OpenIddict, not bound by ASP.NET Core.</summary>
/// <remarks>
/// OpenIddict parses authorize, token, userinfo and logout requests itself, so ASP.NET Core sees no parameters
/// to describe. These helpers put the protocol's real parameters and responses into the generated document
/// without binding anything the handlers do not use.
/// </remarks>
public static class OpenApiExtensions
{
    /// <summary>Documents <paramref name="parameters"/> as query string parameters.</summary>
    public static RouteHandlerBuilder WithQueryParameters(this RouteHandlerBuilder builder, IReadOnlyList<ProtocolParameter> parameters) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            foreach (var parameter in parameters)
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = parameter.Name,
                    In = ParameterLocation.Query,
                    Required = parameter.Required,
                    Description = parameter.Description,
                    Schema = StringSchema(parameter),
                });
            }

            return Task.CompletedTask;
        });

    /// <summary>Documents <paramref name="parameters"/> as an <c>application/x-www-form-urlencoded</c> request body.</summary>
    public static RouteHandlerBuilder WithFormParameters(this RouteHandlerBuilder builder, IReadOnlyList<ProtocolParameter> parameters) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            var schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>(),
                Required = new HashSet<string>(),
            };
            foreach (var parameter in parameters)
            {
                var property = StringSchema(parameter);
                property.Description = parameter.Description;
                schema.Properties[parameter.Name] = property;
                if (parameter.Required)
                    schema.Required.Add(parameter.Name);
            }

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/x-www-form-urlencoded"] = new() { Schema = schema },
                },
            };
            return Task.CompletedTask;
        });

    /// <summary>Documents a <c>302 Found</c> response whose <c>Location</c> header is described by <paramref name="description"/>.</summary>
    public static RouteHandlerBuilder ProducesRedirect(this RouteHandlerBuilder builder, string description) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Responses ??= [];

            // A handler returning IResult with no declared 200 gets an inferred, empty "OK"; a redirect never sends it.
            if (operation.Responses.TryGetValue("200", out var ok) && ok.Content is not { Count: > 0 })
                operation.Responses.Remove("200");

            operation.Responses["302"] = new OpenApiResponse
            {
                Description = description,
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["Location"] = new OpenApiHeader
                    {
                        Description = "Where the browser goes next.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
                    },
                },
            };
            return Task.CompletedTask;
        });

    /// <summary>Replaces the generic description of the <paramref name="statusCode"/> response with <paramref name="description"/>.</summary>
    public static RouteHandlerBuilder WithResponseDescription(this RouteHandlerBuilder builder, int statusCode, string description) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.Responses?.TryGetValue(statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture), out var response) == true &&
                response is OpenApiResponse concrete)
            {
                concrete.Description = description;
            }

            return Task.CompletedTask;
        });

    private static OpenApiSchema StringSchema(ProtocolParameter parameter)
    {
        var schema = new OpenApiSchema { Type = JsonSchemaType.String };
        if (parameter.Allowed.Length > 0)
            schema.Enum = [.. parameter.Allowed.Select(value => (JsonNode)JsonValue.Create(value))];
        return schema;
    }
}
