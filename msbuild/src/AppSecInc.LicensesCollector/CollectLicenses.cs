﻿using System;
using System.Collections.Generic;
using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using System.IO;
using SharpSvn;
using System.Reflection;

namespace AppSecInc.LicensesCollector
{
    /// <summary>
    /// Custom MSBuild task to gather and export
    /// the third party licenses.
    /// </summary>
    public class CollectLicenses : Task
    {
        private String _src;
        [Required]
        public String Src
        {
            get { return _src; }
            set { _src = value; }
        }

        private String _toDir;
        [Required]
        public String ToDir
        {
            get { return _toDir; }
            set { _toDir = value; }
        }

        private Int32 _maxDepth = 1;
        [Required]
        public Int32 MaxDepth
        {
            get { return _maxDepth; }
            set { _maxDepth = value; }
        }

        private LicenseManager _manager = new LicenseManager();
        private Externals _externals;
        private Folders _folders;

        private String _xslFile;
        public String XslFile
        {
            get { return _xslFile; }
            set { _xslFile = value; }
        }

        public override bool Execute()
        {
            if (_src == null)
            {
                throw new Exception("license-files: missing 'src'");
                return false;
            }

            if (_toDir == null)
            {
                throw new Exception("license-files: missing 'toDir'");
                return false;
            }

            if (_xslFile == null)
            {
                //Look in the same folder as the dll file.
                Uri codeBasePath = new Uri(Path.GetDirectoryName(Assembly.GetExecutingAssembly().CodeBase), UriKind.Absolute);
                String path = codeBasePath.AbsolutePath;
                String xslFilePath = path + @"\manifest.xsl";
                FileInfo xslFile = new FileInfo(xslFilePath);
                _xslFile = xslFile.FullName;
            }

            Log.LogMessage(MessageImportance.Normal, "license-files: collecting license files in " + _src, null);

            DirectoryInfo src = new DirectoryInfo(_src.ToString());
            DirectoryInfo toDir = new DirectoryInfo(_toDir.ToString());
            if (!toDir.Exists)
                toDir.Create();

            LicenseFilesManifest manifest = new LicenseFilesManifest();

            SortedList<String, String> externals = getExternalsVersions(src);
            SortedList<String, List<LicenseFound>> licenses = new SortedList<String, List<LicenseFound>>();
            foreach (String external in externals.Keys)
            {
                if (!isIncluded(external))
                {
                    Log.LogMessage(MessageImportance.Normal, "skipping license file in '" + external + "'", null);
                    continue;
                }
                String version;
                externals.TryGetValue(external, out version);
                List<LicenseFound> licensesCollected = collect(external, external,
                    external, version, new DirectoryInfo(src + @"\" + external), 1);
                if (licensesCollected != null)
                {
                    licenses.Add(external, licensesCollected);
                    foreach (LicenseFound licenseFound in licensesCollected)
                    {
                        manifest.Add(getLicenseInfo(external, licenseFound));
                    }
                }
            }

            foreach (String external in externals.Keys)
            {
                if (!licenses.ContainsKey(external) && isIncluded(external))
                {
                    String version;
                    externals.TryGetValue(external, out version);
                    Log.LogMessage(MessageImportance.Normal, "missing license file in '" + external + " (" + version + ")'", null);
                    manifest.Add(getLicenseInfo(external, new LicenseInfo(external, version)));
                }
            }

            foreach (List<LicenseFound> licensesFound in licenses.Values)
            {
                foreach (LicenseFound licenseFound in licensesFound)
                {
                    String licenseFilename = licenseFound.LicenseFilename;
                    FileInfo destinationFile = new FileInfo(toDir.FullName + @"\" + getLicenseFilename(licenseFilename));
                    File.Copy(licenseFound.File, destinationFile.FullName, true);
                }
            }

            FileInfo manifestFile = new FileInfo(toDir + @"\manifest.xml");
            Log.LogMessage(MessageImportance.Normal, "writing " + manifestFile.FullName, null);
            manifest.WriteTo(manifestFile, _xslFile);
            Log.LogMessage(MessageImportance.Normal, manifest.ToString(), null);

            return true;
        }

        private List<LicenseFound> collect(String root, String path, String product, String version, DirectoryInfo src, int depth)
        {
            List<LicenseFound> licenses = new List<LicenseFound>();
            List<LicenseFound> licensesFound = _manager.Find(root, path, product, version, src.FullName, depth);
            if (licensesFound != null)
            {
                Log.LogMessage(MessageImportance.Normal,
                    "found " + licensesFound.Count + " license(s) in '" + path + "'", null);
                licenses.AddRange(licensesFound);
            }

            if (depth < _maxDepth && licenses.Count == 0)
            {
                FileInfo[] files = src.GetFiles();

                foreach (FileInfo file in files)
                {
                    if (file.Name.StartsWith("."))
                        continue;

                    if (!isFolder(src + @"\" + file.Name))
                        continue;

                    DirectoryInfo sub = new DirectoryInfo(src + @"\" + file.Name);
                    List<LicenseFound> licensesCollected = collect(root, path + "/" + sub.Name,
                            product, version, sub, depth + 1);

                    if (licensesCollected != null)
                        licenses.AddRange(licensesCollected);
                }
            }

            return licenses.Count > 0 ? licenses : null;
        }

        private String getVersion(String p, String s)
        {
            s = s.Trim();
            if (s.StartsWith("-r"))
            {
                s = s.Substring("-r".Length).Trim();
                while (s.Length > 0 && Char.IsDigit(s[0]))
                {
                    s = s.Substring(1);
                }
            }

            String version = "";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (Char.IsDigit(c))
                {
                    version += c;
                }
                else if (c == '.' && version.Length > 0)
                {
                    if (version.Length > 0 || Char.IsDigit(version[version.Length - 1]))
                    {
                        version += c;
                    }
                }
                //if the license number is separated by (_)
                else if (c == '_' && version.Length > 0)
                {
                    if (version.Length > 0 || Char.IsDigit(version[version.Length - 1]))
                    {
                        version += c;
                    }
                }
            }

            while (version.Length > 0 && version[version.Length - 1] == '.')
            {
                version = version.Substring(0, version.Length - 1);
            }

            version = version.Replace('_', '.');
            return version.Length > 0 ? version : null;

        }

        private SortedList<String, String> getExternalsVersions(DirectoryInfo src)
        {
            SortedList<String, String> externals = new SortedList<String, String>();
            Log.LogMessage(MessageImportance.Normal, "fetching svn:externals for '", null);

            using (SvnClient svn = new SvnClient())
            {
                String svnExternalsData;
                svn.GetProperty(src.FullName, SvnPropertyNames.SvnExternals, out svnExternalsData);
                Char[] separators = { '\n', '\r' };
                String[] svnExternalsLineData = svnExternalsData.Split(separators);

                foreach (String svnExternal in svnExternalsLineData)
                {
                    String[] parts = svnExternal.Split(new Char[] { ' ' }, 2);
                    if (parts.Length == 2)
                    {
                        String version = getVersion(parts[0], parts[1]);
                        if (version != null)
                            externals.Add(parts[0], version);
                    }
                }
            }

            return externals;
        }

        private String getLicenseFilename(String filename)
        {
            if (_folders != null)
            {
                foreach (Folder folder in _folders)
                {
                    filename = folder.Replace(filename);
                }
            }
            return filename;
        }

        private LicenseInfo getLicenseInfo(String external, ILicense licenseFound)
        {
            LicenseInfo licenseInfo = new LicenseInfo();
            licenseInfo.LicenseFilename = licenseFound.LicenseFilename;
            licenseInfo.LicenseType = licenseFound.LicenseType;
            licenseInfo.Product = licenseFound.Product;
            licenseInfo.SubProduct = licenseFound.SubProduct;
            licenseInfo.Version = licenseFound.Version;

            if (_externals != null)
            {
                External externalDefinition;
                _externals.TryGetValue(external, out externalDefinition);
                if (externalDefinition != null)
                {
                    externalDefinition.Apply(licenseInfo);
                }
            }

            if (_folders != null)
            {
                foreach (Folder folder in _folders)
                {
                    licenseInfo.Product = folder.Replace(licenseInfo.Product);
                    licenseInfo.SubProduct = folder.Replace(licenseInfo.SubProduct);
                    licenseInfo.LicenseFilename = folder.Replace(licenseInfo.LicenseFilename);
                }
            }

            return licenseInfo;
        }

        private Boolean isIncluded(String root)
        {
            if (_externals == null)
                return true;

            External external;
            _externals.TryGetValue(root, out external);
            if (external == null)
                return true;

            return external.Include;
        }

        void AddConfiguredExternals(Externals set)
        {
            if (_externals != null)
            {
                throw new Exception("Only one externals set allowed.");
            }
            _externals = set;
        }

        void AddConfiguredFolders(Folders set)
        {
            if (_folders != null)
            {
                throw new Exception("Only one externals set allowed.");
            }
            _folders = set;
        }

        private bool isFolder(string path)
        {
            return ((File.GetAttributes(path) & FileAttributes.Directory) == FileAttributes.Directory);
        }
    }
}