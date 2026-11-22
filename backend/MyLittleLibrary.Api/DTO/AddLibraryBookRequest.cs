using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

public sealed class AddLibraryBookRequest
{
    [FromForm(Name = "editionId")]
    [Required]
    public string EditionId { get; set; } = "";

    [FromForm(Name = "cover")]
    public IFormFile? Cover { get; set; }
}
