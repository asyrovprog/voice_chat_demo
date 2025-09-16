using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

public sealed class RealtimeModelsOptions
{
    public const string SectionName = "RealtimeModels";

    public Dictionary<string, Template> Templates { get; set; } = new();

    public sealed class Template
    {
        [Required]
        public string ModelId { get; set; }

        [Required]
        public string Voice { get; set; }

        [Required]
        public string Instructions { get; set; }

        [Required]
        public string TranscriptInstructions { get; set; }

        [Required]
        public double Temperature { get; set; }

        public string Language { get; set; } = "en";
    }
}
