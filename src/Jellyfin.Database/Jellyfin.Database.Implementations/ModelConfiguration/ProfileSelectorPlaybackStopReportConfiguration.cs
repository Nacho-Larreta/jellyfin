using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for <see cref="ProfileSelectorPlaybackStopReport"/>.
    /// </summary>
    public class ProfileSelectorPlaybackStopReportConfiguration : IEntityTypeConfiguration<ProfileSelectorPlaybackStopReport>
    {
        /// <inheritdoc />
        public void Configure(EntityTypeBuilder<ProfileSelectorPlaybackStopReport> builder)
        {
            builder.HasKey(entity => entity.ReportKey);

            builder.HasIndex(entity => entity.SwitchId);

            builder
                .HasOne<ProfileSelectorSwitchOperation>()
                .WithMany()
                .HasForeignKey(entity => entity.SwitchId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
