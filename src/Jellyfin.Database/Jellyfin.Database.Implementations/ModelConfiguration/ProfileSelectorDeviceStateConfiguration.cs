using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for <see cref="ProfileSelectorDeviceState"/>.
    /// </summary>
    public class ProfileSelectorDeviceStateConfiguration : IEntityTypeConfiguration<ProfileSelectorDeviceState>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProfileSelectorDeviceState> builder)
        {
            builder
                .HasIndex(entity => new { entity.ProfileSelectorId, entity.DeviceId })
                .IsUnique();

            builder
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(entity => entity.ActiveProfileUserId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
