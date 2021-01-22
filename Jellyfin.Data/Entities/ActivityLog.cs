using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Jellyfin.Data.Interfaces;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Data.Entities
{
    /// <summary>
    /// An entity referencing an activity log entry.
    /// </summary>
    public class ActivityLog : IHasConcurrencyToken
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ActivityLog"/> class.
        /// Public constructor with required data.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="type">The type.</param>
        /// <param name="userId">The user id.</param>
        /// <param name="logLevel">The log level.</param>
        public ActivityLog(string name, string type, Guid userId, LogLevel logLevel = LogLevel.Information)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentNullException(nameof(type));
            }

            Name = name;
            Type = type;
            UserId = userId;
            DateCreated = DateTime.UtcNow;
            LogSeverity = logLevel;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivityLog"/> class.
        /// Default constructor. Protected due to required properties, but present because EF needs it.
        /// </summary>
        protected ActivityLog()
        {
        }

        /// <summary>
        /// Gets or sets the identity of this instance.
        /// This is the key in the backing database.
        /// </summary>
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; protected set; }

        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        /// <remarks>
        /// Required, Max length = 512.
        /// </remarks>
        [Required]
        [MaxLength(512)]
        [StringLength(512)]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the overview.
        /// </summary>
        /// <remarks>
        /// Max length = 512.
        /// </remarks>
        [MaxLength(512)]
        [StringLength(512)]
        public string Overview { get; set; }

        /// <summary>
        /// Gets or sets the short overview.
        /// </summary>
        /// <remarks>
        /// Max length = 512.
        /// </remarks>
        [MaxLength(512)]
        [StringLength(512)]
        public string ShortOverview { get; set; }

        /// <summary>
        /// Gets or sets the type.
        /// </summary>
        /// <remarks>
        /// Required, Max length = 256.
        /// </remarks>
        [Required]
        [MaxLength(256)]
        [StringLength(256)]
        public string Type { get; set; }

        /// <summary>
        /// Gets or sets the user id.
        /// </summary>
        /// <remarks>
        /// Required.
        /// </remarks>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the item id.
        /// </summary>
        /// <remarks>
        /// Max length = 256.
        /// </remarks>
        [MaxLength(256)]
        [StringLength(256)]
        public string ItemId { get; set; }

        /// <summary>
        /// Gets or sets the date created. This should be in UTC.
        /// </summary>
        /// <remarks>
        /// Required.
        /// </remarks>
        public DateTime DateCreated { get; set; }

        /// <summary>
        /// Gets or sets the log severity. Default is <see cref="LogLevel.Trace"/>.
        /// </summary>
        /// <remarks>
        /// Required.
        /// </remarks>
        public LogLevel LogSeverity { get; set; }

        /// <inheritdoc />
        [ConcurrencyCheck]
        public uint RowVersion { get; set; }

        /// <inheritdoc />
        public void OnSavingChanges()
        {
            RowVersion++;
        }
    }
}
