using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for <see cref="ProfileSelector"/>.
    /// </summary>
    public class ProfileSelectorConfiguration : IEntityTypeConfiguration<ProfileSelector>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProfileSelector> builder)
        {
            builder
                .HasIndex(entity => entity.OwnerUserId)
                .IsUnique();

            builder
                .HasMany(entity => entity.Members)
                .WithOne()
                .HasForeignKey(entity => entity.ProfileSelectorId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(entity => entity.DeviceStates)
                .WithOne()
                .HasForeignKey(entity => entity.ProfileSelectorId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(entity => entity.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
