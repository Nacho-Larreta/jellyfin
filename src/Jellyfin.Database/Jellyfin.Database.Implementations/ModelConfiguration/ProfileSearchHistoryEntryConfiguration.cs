using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for <see cref="ProfileSearchHistoryEntry"/>.
    /// </summary>
    public class ProfileSearchHistoryEntryConfiguration : IEntityTypeConfiguration<ProfileSearchHistoryEntry>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProfileSearchHistoryEntry> builder)
        {
            builder.ToTable("ProfileSearchHistoryEntries");

            builder
                .HasIndex(entity => new { entity.OwnerUserId, entity.ProfileUserId, entity.SearchTermNormalized })
                .IsUnique();

            builder
                .HasIndex(entity => new { entity.OwnerUserId, entity.ProfileUserId, entity.LastSearchedUtc });

            builder
                .Property(entity => entity.SearchTerm)
                .HasMaxLength(255)
                .IsRequired();

            builder
                .Property(entity => entity.SearchTermNormalized)
                .HasMaxLength(255)
                .IsRequired();

            builder
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(entity => entity.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(entity => entity.ProfileUserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
