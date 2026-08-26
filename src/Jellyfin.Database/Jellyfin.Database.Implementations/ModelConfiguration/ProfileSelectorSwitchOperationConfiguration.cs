using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for <see cref="ProfileSelectorSwitchOperation"/>.
    /// </summary>
    public class ProfileSelectorSwitchOperationConfiguration : IEntityTypeConfiguration<ProfileSelectorSwitchOperation>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProfileSelectorSwitchOperation> builder)
        {
            builder.HasKey(entity => entity.SwitchId);

            builder
                .HasIndex(entity => new { entity.ProfileSelectorId, entity.ActiveDeviceId })
                .IsUnique()
                .HasFilter("ActiveDeviceId IS NOT NULL");

            builder.HasIndex(entity => entity.RetainUntilUtc);

            builder.HasIndex(entity => new
            {
                entity.CallerUserId,
                entity.DeviceId,
                entity.RetainUntilUtc
            });

            builder
                .HasOne<ProfileSelector>()
                .WithMany()
                .HasForeignKey(entity => entity.ProfileSelectorId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
