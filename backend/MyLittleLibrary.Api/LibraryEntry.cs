using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

public sealed class LibraryEntry
{
    public const string CoverAttachmentName = "cover";
    public required string OwnerKey { get; init; }
    public required string BookId { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public bool HasLocalCover { get; init; }

    public static string DocumentId(string ownerKey, string editionId) => $"LibraryEntries/{ownerKey}/{editionId}";
}

public sealed class AddLibraryBookRequest
{
    [FromForm(Name = "workId")]
    [Required]
    public string WorkId { get; set; } = "";

    [FromForm(Name = "editionId")]
    [Required]
    public string EditionId { get; set; } = "";

    [FromForm(Name = "cover")]
    public IFormFile? Cover { get; set; }
}