namespace HelixPythonNamespace.AddIns.Kit
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A designer whose toolbox can be folded up, and whose folds are worth remembering.
    ///
    /// <para><b>Why an interface.</b> A designer can expose its folded categories through one contract,
    /// allowing the shell to restore and persist that state consistently.</para>
    ///
    /// <para><b>Category NAMES, not indices.</b> A palette is rebuilt whenever its catalogue
    /// changes, and a saved index would then restore the fold onto whatever category had moved into
    /// that slot. A name that no longer exists is simply ignored, which is the right answer for a
    /// category that has been renamed or removed.</para>
    /// </summary>
    public interface IToolboxCollapseState
    {
        /// <summary>
        /// The categories currently folded away. Setting it applies exactly that set: anything
        /// named is folded, anything not named is opened.
        /// </summary>
        IList<string> CollapsedToolboxCategories { get; set; }

        /// <summary>Raised when the user folds or unfolds a category.</summary>
        event EventHandler ToolboxCollapseChanged;
    }
}
