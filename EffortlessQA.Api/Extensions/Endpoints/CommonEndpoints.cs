using EffortlessQA.Api.Services.Implementation;
using EffortlessQA.Data.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EffortlessQA.Api.Extensions
{
    public static partial class ApiExtensions
    {
        public static void MapCommonEndpoints(this WebApplication app)
        {
            // POST /api/v1/common/generate-pdf
            app.MapPost(
                    "/api/v1/common/generate-pdf",
                    async ([FromBody] PdfGenerationDto dto, HttpContext context) =>
                    {
                        try
                        {
                            // Validate tenant ID
                            var tenantId = context.User.FindFirst("TenantId")?.Value;
                            if (string.IsNullOrEmpty(tenantId))
                            {
                                return Results.Unauthorized();
                            }

                            // Validate input
                            if (dto.Columns == null || !dto.Columns.Any())
                            {
                                return Results.BadRequest(
                                    new ApiResponse<object>
                                    {
                                        Error = new ErrorResponse
                                        {
                                            Code = "BadRequest",
                                            Message = "At least one column definition is required."
                                        }
                                    }
                                );
                            }

                            if (dto.Data == null)
                            {
                                return Results.BadRequest(
                                    new ApiResponse<object>
                                    {
                                        Error = new ErrorResponse
                                        {
                                            Code = "BadRequest",
                                            Message = "Data cannot be null."
                                        }
                                    }
                                );
                            }

                            // Generate PDF
                            var pdfBytes = GeneratePdf(dto);

                            // Return as file
                            return Results.File(
                                pdfBytes,
                                contentType: "application/pdf",
                                fileDownloadName: dto.FileName
                            );
                        }
                        catch (Exception ex)
                        {
                            return Results.BadRequest(
                                new ApiResponse<object>
                                {
                                    Error = new ErrorResponse
                                    {
                                        Code = "BadRequest",
                                        Message = ex.Message
                                    }
                                }
                            );
                        }
                    }
                )
                .WithName("GeneratePdf")
                .RequireAuthorization("TesterOrAdmin")
                .WithTags(COMMON_TAG)
                .WithMetadata();

            app.MapPost(
                    "/api/v1/common/images/upload",
                    async (
                        HttpContext context,
                        AzureBlobStorageService blobStorageService,
                        IFormFile file,
                        [FromQuery] string entityId,
                        [FromQuery] string fieldName,
                        [FromQuery] string ProjectId,
                        [FromQuery] string EntityType
                    ) =>
                    {
                        try
                        {
                            var tenantId = context.User.FindFirst("TenantId")?.Value;
                            if (string.IsNullOrEmpty(tenantId))
                                return Results.Unauthorized();

                            if (file == null || file.Length == 0)
                                return Results.BadRequest(new { Error = "No file uploaded." });

                            using var stream = file.OpenReadStream();
                            var imageUrl = await blobStorageService.UploadImageAsync(
                                stream,
                                file.FileName,
                                entityId,
                                fieldName,
                                tenantId,
                                ProjectId,
                                EntityType
                            );

                            return Results.Ok(new { location = imageUrl });
                        }
                        catch (Exception ex)
                        {
                            return Results.BadRequest(new { Error = ex.Message });
                        }
                    }
                )
                .WithName("UploadImage")
                .DisableAntiforgery()
                .RequireAuthorization()
                .WithTags("Images");

            app.MapPost(
                    "/api/v1/common/images/delete",
                    async (
                        HttpContext context,
                        AzureBlobStorageService blobStorageService,
                        [FromForm] string imageUrl,
                        [FromForm] string tenantId,
                        [FromForm] string entityId,
                        [FromForm] string fieldName,
                        [FromForm] string ProjectId,
                        [FromForm] string EntityType
                    ) =>
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(imageUrl))
                                return Results.BadRequest(
                                    new { Error = "TenantId and imageUrl are required." }
                                );

                            // Extract blob name (file name only) from SAS URL
                            var blobName = new Uri(imageUrl).AbsolutePath.TrimStart('/');
                            var blobPathParts = blobName.Split('/');
                            if (blobPathParts.Length < 6)
                                return Results.BadRequest(
                                    new { Error = "Invalid blob URL format." }
                                );

                            var uniqueBlobName = blobPathParts[^1]; // e.g., "843347e7-7340-4fcb-bde3-bdec07359e74_image.png"
                            Console.WriteLine(
                                $"Extracted blob name: {uniqueBlobName}, Full path: {blobName}"
                            );

                            await blobStorageService.DeleteBlobAsync(
                                uniqueBlobName,
                                tenantId,
                                ProjectId,
                                EntityType,
                                entityId,
                                fieldName
                            );

                            return Results.Ok(new { Message = "Image deleted successfully." });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error in delete endpoint: {ex.Message}");
                            return Results.BadRequest(new { Error = ex.Message });
                        }
                    }
                )
                .WithName("DeleteImage")
                .DisableAntiforgery()
                .WithTags("Images"); // Removed RequireAuthorization()
        }

        private static byte[] GeneratePdf(PdfGenerationDto dto)
        {
            // Configure QuestPDF
            QuestPDF.Settings.License = LicenseType.Community; // Use appropriate license

            // Create document
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(dto.FontSizeBody));

                    // Header
                    page.Header().Text(dto.Title).FontSize(dto.FontSizeTitle).Bold().AlignCenter();

                    // Content (Table)
                    page.Content()
                        .Table(table =>
                        {
                            // Define columns
                            table.ColumnsDefinition(columns =>
                            {
                                foreach (var col in dto.Columns)
                                {
                                    columns.ConstantColumn(150); // Adjust width as needed
                                }
                            });

                            // Header row
                            table.Header(header =>
                            {
                                foreach (var col in dto.Columns)
                                {
                                    header
                                        .Cell()
                                        .Border(1)
                                        .Padding(5)
                                        .Text(col.Header)
                                        .FontSize(dto.FontSizeHeader)
                                        .Bold();
                                }
                            });

                            // Data rows
                            foreach (var row in dto.Data)
                            {
                                foreach (var col in dto.Columns)
                                {
                                    var value = row.ContainsKey(col.Field) ? row[col.Field] : "";
                                    table
                                        .Cell()
                                        .Border(1)
                                        .Padding(5)
                                        .Text(value)
                                        .FontSize(dto.FontSizeBody);
                                }
                            }
                        });

                    // Footer
                    page.Footer()
                        .AlignCenter()
                        .Text(x =>
                        {
                            x.Span("Page ");
                            x.CurrentPageNumber();
                            x.Span(" of ");
                            x.TotalPages();
                        });
                });
            });

            // Generate PDF as byte array
            return document.GeneratePdf();
        }
    }
}
