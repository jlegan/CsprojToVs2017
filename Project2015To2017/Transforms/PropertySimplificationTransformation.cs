using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Project2015To2017.Definition;
using Project2015To2017.Reading;

namespace Project2015To2017.Transforms
{
	public sealed class PropertySimplificationTransformation : ITransformation
	{
		public void Transform(Project definition, ILogger logger)
		{
			if (definition.PrimaryPropertyGroup == null)
			{
				throw new InvalidOperationException("Unable to do property simplification pass due to lack of primary PropertyGroup.");
			}

			// special case handling for when condition-guarded props override global props not set to their defaults
			var globalOverrides = RetrieveGlobalOverrides(definition.PrimaryPropertyGroup);

			if (string.IsNullOrEmpty(definition.ProjectName))
			{
				if (!globalOverrides.TryGetValue("ProjectName", out var projectName))
					if (!globalOverrides.TryGetValue("AssemblyName", out projectName))
						projectName = Path.GetFileNameWithoutExtension(definition.FilePath.Name);
				definition.ProjectName = projectName;
			}

			FilterUnneededProperties(definition, definition.PrimaryPropertyGroup, globalOverrides);
			foreach (var propertyGroup in definition.AdditionalPropertyGroups)
			{
				FilterUnneededProperties(definition, propertyGroup, globalOverrides);
			}
		}

		private static void FilterUnneededProperties(Project project,
			XElement propertyGroup,
			IDictionary<string, string> globalOverrides)
		{
			var removeQueue = new List<XElement>();

			foreach (var child in propertyGroup.Elements())
			{
				var parentCondition = propertyGroup.Attribute("Condition")?.Value.Trim() ?? "";
				var hasParentCondition = parentCondition.Length > 1; // no sane condition is 1 char long
				var parentConditionEvaluated =
					ConditionEvaluator.GetNonAmbiguousConditionContracts(parentCondition);
				var parentConditionHasPlatform =
					parentConditionEvaluated.TryGetValue("Platform", out var parentConditionPlatform);
				var parentConditionHasConfiguration =
					parentConditionEvaluated.TryGetValue("Configuration", out var parentConditionConfiguration);
				var parentConditionPlatformLower = parentConditionPlatform?.ToLowerInvariant();
				var parentConditionConfigurationLower = parentConditionConfiguration?.ToLowerInvariant();
				var isDebugOnly = parentConditionHasConfiguration && parentConditionConfigurationLower == "debug";
				var isReleaseOnly = parentConditionHasConfiguration &&
				                    parentConditionConfigurationLower == "release";

				var tagLocalName = child.Name.LocalName;
				var valueLower = child.Value.ToLowerInvariant();
				var valueLowerTrim = valueLower.Trim();
				var emptyValue = valueLowerTrim.Length == 0;
				var hasGlobalOverride = globalOverrides.TryGetValue(tagLocalName, out var globalOverride);
				var globalOverrideLower = globalOverride?.ToLowerInvariant();

				// Regex is the easiest way to replace string between two unknown chars preserving both as is
				// so that bin\Debug\net472 is turned into bin\$(Configuration)\net472
				// and bin/Debug\net472 is turned into bin/$(Configuration)\net472, preserving path separators
				var configurationPathRegex = parentConditionHasConfiguration
					? new Regex($@"([\\/]){parentConditionConfiguration}([\\/])")
					: null;
				var platformPathRegex = parentConditionHasPlatform
					? new Regex($@"([\\/]){parentConditionPlatform}([\\/])")
					: null;

				var childCondition = child.Attribute("Condition");
				switch (tagLocalName)
				{
					// VS2013 NuGet bugs workaround
					case "NuGetPackageImportStamp":
					// used by Project2015To2017 and not needed anymore
					case "OutputType" when !hasParentCondition:
					case "Platforms" when !hasParentCondition:
					case "Configurations" when !hasParentCondition:
					case "TargetFrameworkVersion" when !hasParentCondition:
					case "TargetFrameworkIdentifier" when !hasParentCondition:
					case "TargetFrameworkProfile" when !hasParentCondition && emptyValue:
					// VCS properties
					case "SccProjectName" when !hasParentCondition && emptyValue:
					case "SccLocalPath" when !hasParentCondition && emptyValue:
					case "SccAuxPath" when !hasParentCondition && emptyValue:
					case "SccProvider" when !hasParentCondition && emptyValue:
					// Project properties set to defaults (Microsoft.NET.Sdk)
					case "OutputType" when ValidateDefaultValue("Library"):
					case "FileAlignment" when ValidateDefaultValue("512"):
					case "ErrorReport" when ValidateDefaultValue("prompt"):
					case "Deterministic" when ValidateDefaultValue("true"):
					case "WarningLevel" when ValidateDefaultValue("4"):
					case "DebugType" when ValidateDefaultValue("portable"):
					case "ResolveNuGetPackages" when ValidateDefaultValue("false"):
					case "SkipImportNuGetProps" when ValidateDefaultValue("true"):
					case "SkipImportNuGetBuildTargets" when ValidateDefaultValue("true"):
					case "RestoreProjectStyle" when ValidateDefaultValue("packagereference"):
					case "AllowUnsafeBlocks" when ValidateDefaultValue("false"):
					case "TreatWarningsAsErrors" when ValidateDefaultValue("false"):
					case "Prefer32Bit" when ValidateDefaultValue("false"):
					case "SignAssembly" when ValidateDefaultValue("false"):
					case "DelaySign" when ValidateDefaultValue("false"):
					case "GeneratePackageOnBuild" when ValidateDefaultValue("false"):
					case "PackageRequireLicenseAcceptance" when ValidateDefaultValue("false"):
					case "DebugSymbols" when ValidateDefaultValue("false"):
					case "CheckForOverflowUnderflow" when ValidateDefaultValue("false"):
					case "AppendTargetFrameworkToOutputPath" when ValidateDefaultValue("true"):
					case "AppDesignerFolder" when ValidateDefaultValue("properties"):
					case "DefaultProjectTypeGuid"
						when ValidateDefaultValue("{fae04ec0-301f-11d3-bf4b-00c04f79efbc}"):
					case "DefaultLanguageSourceExtension" when ValidateDefaultValue(".cs"):
					case "Language" when ValidateDefaultValue("C#"):
					case "TargetRuntime" when ValidateDefaultValue("managed"):
					case "Utf8Output" when ValidateDefaultValue("true"):
					case "PlatformName" when ValidateDefaultValue("$(platform)")
					                         || (parentConditionHasPlatform &&
					                             ValidateDefaultValue(parentConditionPlatformLower)):
					// Conditional platform default values
					case "PlatformTarget" when parentConditionHasPlatform && child.Value == parentConditionPlatform
					                                                      && !hasGlobalOverride:
					// Conditional configuration (Debug/Release) default values
					case "DefineConstants"
						when isDebugOnly && ValidateDefaultConstants(valueLower, "debug", "trace") &&
						     !hasGlobalOverride:
					case "DefineConstants" when isReleaseOnly && ValidateDefaultConstants(valueLower, "trace") &&
					                            !hasGlobalOverride:
					case "Optimize" when isDebugOnly && valueLower == "false" && !hasGlobalOverride:
					case "Optimize" when isReleaseOnly && valueLower == "true" && !hasGlobalOverride:
					case "DebugSymbols" when isDebugOnly && valueLower == "true" && !hasGlobalOverride:
					// Default project values for Platform and Configuration
					case "Platform" when !hasParentCondition && ValidateEmptyConditionValue(childCondition) &&
					                     valueLower == "anycpu":
					case "Configuration" when !hasParentCondition && ValidateEmptyConditionValue(childCondition) &&
					                          valueLower == "debug":
					// Extra ProjectName duplicates
					case "RootNamespace" when !hasParentCondition && ValidateEmptyConditionValue(childCondition) &&
					                          child.Value == project.ProjectName:
					case "AssemblyName" when !hasParentCondition && ValidateEmptyConditionValue(childCondition) &&
					                         child.Value == project.ProjectName:
					{
						removeQueue.Add(child);
						break;
					}
				}

				if (parentConditionHasConfiguration || parentConditionHasPlatform)
				{
					switch (tagLocalName)
					{
						case "OutputPath":
						case "IntermediateOutputPath":
						case "DocumentationFile":
						case "CodeAnalysisRuleSet":
						{
							child.Value = ReplaceWithPlaceholders(child.Value);

							break;
						}
					}
				}

				// following local methods will capture parent scope
				bool ValidateDefaultValue(string @default)
				{
					return (!hasParentCondition && valueLower == @default) ||
					       (hasParentCondition && ValidateConditionedDefaultValue(@default));
				}

				bool ValidateConditionedDefaultValue(string @default)
				{
					return (valueLower == @default) && (!hasGlobalOverride || globalOverrideLower == @default);
				}

				string ReplaceWithPlaceholders(string value)
				{
					if (parentConditionHasConfiguration)
					{
						value = configurationPathRegex.Replace(value, "$1$(Configuration)$2");
					}

					if (parentConditionHasPlatform)
					{
						value = platformPathRegex.Replace(value, "$1$(Platform)$2");
					}

					return value;
				}
			}

			// we cannot remove elements correctly while iterating through elements, 2nd pass is needed
			foreach (var child in removeQueue)
			{
				child.Remove();
			}
		}

		/// <summary>
		/// Get all non-conditional properties and their respective values
		/// </summary>
		/// <param name="propertyGroup">Primary unconditional PropertyGroup to be inspected</param>
		/// <returns>Dictionary of properties' keys and values</returns>
		private static IDictionary<string, string> RetrieveGlobalOverrides(XElement propertyGroup)
		{
			var globalOverrides = new Dictionary<string, string>();
			foreach (var child in propertyGroup.Elements())
			{
				if (!HasEmptyCondition(child))
				{
					continue;
				}

				globalOverrides[child.Name.LocalName] = child.Value.Trim();
			}

			return globalOverrides;

			bool HasEmptyCondition(XElement element)
			{
				var conditionAttribute = element.Attribute("Condition");
				if (conditionAttribute == null)
				{
					return true;
				}

				var condition = conditionAttribute.Value.Trim() ?? "";

				// no sane condition is 1 char long
				return condition.Length <= 1;
			}
		}

		private static bool ValidateEmptyConditionValue(XAttribute condition)
		{
			if (condition == null)
			{
				return true;
			}

			var value = condition.Value;
			return (value.Count(x => x == '=') == 2) && value.Contains("''");
		}

		private static bool ValidateDefaultConstants(string value, params string[] expected)
		{
			var defines = value.Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries);
			return Helpers.ValidateSet(defines, expected);
		}
	}
}