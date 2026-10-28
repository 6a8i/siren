using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for the User entity.
    /// </summary>
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        /// <inheritdoc/>
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder
                .Property(user => user.Username);

            builder
                .HasOne(u => u.ProfileImage)
                .WithOne()
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(u => u.Permissions)
                .WithOne()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(u => u.Preferences)
                .WithOne()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(u => u.AccessSchedules)
                .WithOne()
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(u => u.DisplayPreferences)
                .WithOne()
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(u => u.ItemDisplayPreferences)
                .WithOne()
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasOne<Family>()
                .WithMany()
                .HasForeignKey(user => user.FamilyId)
                .OnDelete(DeleteBehavior.SetNull);

            builder
                .HasIndex(entity => entity.FamilyId);

            builder
                .HasIndex(entity => entity.Username)
                .IsUnique();

            builder
                .HasIndex(entity => entity.NormalizedUsername)
                .IsUnique();
        }
    }
}
