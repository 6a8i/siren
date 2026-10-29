using System;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a device is signed out of a family by a family administrator or the
/// server administrator.
/// </summary>
public class FamilyDeviceRemovedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyDeviceRemovedEventArgs"/> class.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="deviceId">The device id.</param>
    /// <param name="deviceName">The device name.</param>
    /// <param name="removedBy">The user who removed the device, or <c>null</c> for an API key.</param>
    public FamilyDeviceRemovedEventArgs(Family family, string deviceId, string deviceName, User? removedBy)
    {
        Family = family;
        DeviceId = deviceId;
        DeviceName = deviceName;
        RemovedBy = removedBy;
    }

    /// <summary>
    /// Gets the family.
    /// </summary>
    public Family Family { get; }

    /// <summary>
    /// Gets the device id.
    /// </summary>
    public string DeviceId { get; }

    /// <summary>
    /// Gets the device name.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Gets the user who removed the device, or <c>null</c> for an API key.
    /// </summary>
    public User? RemovedBy { get; }
}
