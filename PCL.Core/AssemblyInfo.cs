using System.Runtime.CompilerServices;
using System.Windows.Markup;

[assembly: XmlnsDefinition("https://github.com/BeeAndHoney06/pcl2bee/core/ui/animation", "PCL.Core.UI.Animation")]
[assembly: XmlnsDefinition("https://github.com/BeeAndHoney06/pcl2bee/core/ui/animation", "PCL.Core.UI.Animation.Core")]
[assembly: XmlnsDefinition("https://github.com/BeeAndHoney06/pcl2bee/core/ui/animation", "PCL.Core.UI.Animation.Easings")]
[assembly: XmlnsPrefix("https://github.com/BeeAndHoney06/pcl2bee/core/ui/animation", "ani")]

[assembly:XmlnsDefinition("https://github.com/BeeAndHoney06/pcl2bee/core/utils/validate", "PCL.Core.Utils.Validate")]
[assembly:XmlnsPrefix("https://github.com/BeeAndHoney06/pcl2bee/core/utils/validate", "val")]

[assembly: DisableRuntimeMarshalling]
[assembly: InternalsVisibleTo("PCL.Core.Test")]
