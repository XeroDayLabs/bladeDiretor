﻿//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace tests.Properties {
    using System;
    
    
    /// <summary>
    ///   A strongly-typed resource class, for looking up localized strings, etc.
    /// </summary>
    // This class was auto-generated by the StronglyTypedResourceBuilder
    // class via a tool like ResGen or Visual Studio.
    // To add or remove a member, edit your .ResX file then rerun ResGen
    // with the /str option, or rebuild your VS project.
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "4.0.0.0")]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    internal class Resources {
        
        private static global::System.Resources.ResourceManager resourceMan;
        
        private static global::System.Globalization.CultureInfo resourceCulture;
        
        [global::System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal Resources() {
        }
        
        /// <summary>
        ///   Returns the cached ResourceManager instance used by this class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Resources.ResourceManager ResourceManager {
            get {
                if (object.ReferenceEquals(resourceMan, null)) {
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("tests.Properties.Resources", typeof(Resources).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }
        
        /// <summary>
        ///   Overrides the current thread's CurrentUICulture property for all
        ///   resource lookups using this strongly typed resource class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Globalization.CultureInfo Culture {
            get {
                return resourceCulture;
            }
            set {
                resourceCulture = value;
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to &lt;?xml version=&quot;1.0&quot; encoding=&quot;UTF-8&quot;?&gt;
        ///&lt;!--generated by conrep version 3.40--&gt;
        ///&lt;Conrep version=&quot;3.40&quot; originating_platform=&quot;ProLiant SL390s G7&quot; originating_family=&quot;P69&quot; originating_romdate=&quot;05/05/2011&quot; originating_processor_manufacturer=&quot;Intel&quot;&gt;
        ///  &lt;Section name=&quot;IPL_Order&quot; helptext=&quot;Current Initial ProgramLoad device boot order.&quot;&gt;
        ///    &lt;Index0&gt;04 &lt;/Index0&gt;
        ///    &lt;Index1&gt;00 &lt;/Index1&gt;
        ///    &lt;Index2&gt;01 &lt;/Index2&gt;
        ///    &lt;Index3&gt;03 &lt;/Index3&gt;
        ///    &lt;Index4&gt;02 &lt;/Index4&gt;
        ///    &lt;Index5&gt;ff &lt;/Index5&gt;
        ///    &lt;Index6&gt;ff &lt;/In [rest of string was truncated]&quot;;.
        /// </summary>
        internal static string testBIOS {
            get {
                return ResourceManager.GetString("testBIOS", resourceCulture);
            }
        }
    }
}