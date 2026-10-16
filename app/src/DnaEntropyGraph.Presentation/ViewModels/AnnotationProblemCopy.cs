using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>The Resources.resw key for each reason a GFF3 cannot be paired with a FASTA (issue #686). Literal keys, so each string is visibly read.</summary>
public static class AnnotationProblemCopy
{
    public static string KeyFor(AnnotationProblemCode code) => code switch
    {
        AnnotationProblemCode.FileUnreadable => "NewRunAnnotationProblem_FileUnreadable",
        AnnotationProblemCode.FileTooLarge => "NewRunAnnotationProblem_FileTooLarge",
        AnnotationProblemCode.NotFasta => "NewRunAnnotationProblem_NotFasta",
        AnnotationProblemCode.NoFeatures => "NewRunAnnotationProblem_NoFeatures",
        AnnotationProblemCode.NoNameMatch => "NewRunAnnotationProblem_NoNameMatch",
        AnnotationProblemCode.NoneFit => "NewRunAnnotationProblem_NoneFit",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped AnnotationProblemCode: add its copy to Resources.resw and here."),
    };
}
