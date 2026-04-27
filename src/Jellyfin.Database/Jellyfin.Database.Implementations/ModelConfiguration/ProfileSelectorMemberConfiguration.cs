using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for <see cref="ProfileSelectorMember"/>.
    /// </summary>
    public class ProfileSelectorMemberConfiguration : IEntityTypeConfiguration<ProfileSelectorMember>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProfileSelectorMember> builder)
        {
            builder
                .HasIndex(entity => new { entity.ProfileSelectorId, entity.ProfileUserId })
                .IsUnique();

            builder
                .HasIndex(entity => entity.ProfileUserId)
                .IsUnique();

            builder
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(entity => entity.ProfileUserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
