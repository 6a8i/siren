using System;

namespace MediaBrowser.Model.Dto;

/// <summary>
/// A device signed in as a profile of a family, as listed to the family's members.
/// </summary>
public class FamilyDeviceDto
{
    /// <summary>
    /// Gets or sets the device id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the app signed in on the device.
    /// </summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version of the app.
    /// </summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the profile signed in on the device.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the name of the profile signed in on the device.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the device was last seen, in UTC.
    /// </summary>
    public DateTime DateLastActivity { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is the device the caller is using right now.
    /// </summary>
    public bool IsCurrent { get; set; }
}
