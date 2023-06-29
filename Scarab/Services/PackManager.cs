﻿using Scarab.Interfaces;
using Scarab.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using MessageBox.Avalonia.DTO;
using MessageBox.Avalonia.Enums;
using Scarab.Util;

namespace Scarab.Services;

public class PackManager : IPackManager
{
    private readonly ISettings _settings;
    private readonly IModDatabase _db;
    private readonly IInstaller _installer;
    private readonly IFileSystem _fs;
    private readonly IModSource _mods;
    /// <summary>
    /// Private collection to allow constant lookup times of ModItems from name.
    /// </summary>
    private readonly Dictionary<string, ModItem> _items;

    /// <summary>
    /// A list of all the profiles saved by the application.
    /// </summary>
    private readonly SortableObservableCollection<Pack> _packList;
    
    public SortableObservableCollection<Pack> PackList => _packList;

    private const string packInfoFileName = "packMods.json";

    public PackManager(ISettings settings, IInstaller installer, IModDatabase db, IFileSystem fs, IModSource mods)
    {
        _installer = installer;
        _db = db;
        _fs = fs;
        _mods = mods;
        _settings = settings;
        
        _items = _db.Items.Where(x => x.State is not NotInModLinksState { ModlinksMod: false })
            .ToDictionary(x => x.Name, x => x);

        _packList = new SortableObservableCollection<Pack>(FindAvailablePacks());
        _packList.SortBy((pack1, pack2) => string.CompareOrdinal(pack1.Name, pack2.Name));
    }

    /// <summary>
    /// Look through the managed folder for any profiles and add them to the list.
    /// </summary>
    private IEnumerable<Pack> FindAvailablePacks()
    {
        var packs = new List<Pack>();
        
        var packFolderLocation = _settings.ManagedFolder;

        foreach (var folderPath in _fs.Directory.EnumerateDirectories(packFolderLocation))
        {
            var folder = Path.GetFileName(folderPath);
            if (folder == "Mods") continue; // ignore mod folder
            
            var packInfo = Path.Combine(folderPath, packInfoFileName);
            if (!_fs.File.Exists(packInfo)) continue;

            try
            {
                var pack = JsonSerializer.Deserialize<Pack>(File.ReadAllText(packInfo));
                if (pack?.Name == folder) // only add packs with same name as folder
                    packs.Add(pack);
            }
            catch (Exception e)
            {
                Trace.TraceError($"Error reading pack info file {e}");
            }
        }

        return packs;
    }

    /// <summary>
    /// Loads a pack as the main mod list.
    /// </summary>
    /// <param name="packName">The profile to set as current.</param>
    public async Task LoadPack(string packName)
    {
        await EnsureGameClosed();
        
        var pack = _packList.FirstOrDefault(x => x.Name == packName);
        if (pack == null)
        {
            await DisplayErrors.DisplayGenericError("Could not set current profile: profile not found!");
            return;
        }

        var packFolder = Path.Combine(_settings.ManagedFolder, packName);

        // move all the mods to a temp folder so we can revert if the pack enabling fails
        _fs.Directory.Move(_settings.ModsFolder, Path.Combine(_settings.ManagedFolder, "Temp_Mods_Storage"));
        var tempDbMods = _mods.Mods.ToDictionary(x => x.Key, x => x.Value);
        var tempDbNotInModlinksMods = _mods.NotInModlinksMods.ToDictionary(x => x.Key, x => x.Value);
        
        // enable pack
        try
        {
            // move the mods from the profile to the mods folder
            _fs.Directory.Move(packFolder, _settings.ModsFolder);
            await _mods.SetMods(pack.InstalledMods.Mods, pack.InstalledMods.NotInModlinksMods);

            var requestedModList = _mods.Mods.Select(x => x.Key)
                .Concat(_mods.NotInModlinksMods.Select(x => x.Key))
                .ToArray();
            
            // get the full dependency list of the pack
            HashSet<string> fullDependencyList = new();

            // ensure all mods that are supposed to be in the pack are installed
            foreach (string modName in requestedModList)
            {
                if (InstalledMods.ModExists(_settings, modName, out _))
                    continue;
                
                if (_items.TryGetValue(modName, out var correspondingMod))
                {
                    // to ensure the mod is installed when OnInstall is called
                    correspondingMod.State = new NotInstalledState();
                    await _installer.Install(correspondingMod, _ => { }, true);

                    // save full dependency list which will be used to decide what to uninstall
                    foreach (var dep in correspondingMod.Dependencies)
                        AddDependency(fullDependencyList, dep);
                    
                }
                else // can't install so yeet it from the profile
                {
                    _mods.Mods.Remove(modName);
                    _mods.NotInModlinksMods.Remove(modName);
                }
            }

            var currentList = _fs.Directory.EnumerateDirectories(_settings.ModsFolder)
                .Concat(_fs.Directory.EnumerateDirectories(_settings.DisabledFolder));

            // uninstall mods that arent in pack
            foreach (var modNamePath in currentList)
            {
                var modName = Path.GetFileName(modNamePath);
                if (modName == "Disabled") continue; // skip disabled Folder
                
                // don't uninstall mods that are said to be in the pack
                if (_mods.NotInModlinksMods.ContainsKey(modName)) continue;
                if (_mods.Mods.ContainsKey(modName)) continue;

                // don't uninstall if the mod is a dependency of a pack mod
                if (fullDependencyList.Contains(modName)) continue;
                
                // since it isn't a listed pack mod or a dependency, uninstall it
                if (_fs.Directory.Exists(Path.Combine(_settings.ModsFolder, modName)))
                    _fs.Directory.Delete(Path.Combine(_settings.ModsFolder, modName), true);
                if (_fs.Directory.Exists(Path.Combine(_settings.DisabledFolder, modName)))
                    _fs.Directory.Delete(Path.Combine(_settings.DisabledFolder, modName), true);
            }
            
            // cache result so it is easier to load next time
            await SavePack(pack.Name, pack.Description);
            
            // delete temp folder
            _fs.Directory.Delete(Path.Combine(_settings.ManagedFolder, "Temp_Mods_Storage"), true);

        }
        catch (Exception e)
        {
            _fs.Directory.Delete(_settings.ModsFolder, true);
            _fs.Directory.Move(Path.Combine(_settings.ManagedFolder, "Temp_Mods_Storage"), _settings.ModsFolder);
            await _mods.SetMods(tempDbMods, tempDbNotInModlinksMods);
            
            await DisplayErrors.DisplayGenericError("An error occured when activating pack. Scarab will revert to old mods", e);
        }
    }

    /// <summary>
    /// Saves the current Mods folder as a pack. Replaces the pack if it already exists.
    /// </summary>
    public async Task SavePack(string name, string description)
    {
        var packFolder = Path.Combine(_settings.ManagedFolder, name);
        
        if (_fs.Directory.Exists(packFolder)) 
            _fs.Directory.Delete(packFolder, true);
        
        CopyDirectory(_settings.ModsFolder, packFolder);

        var packInfo = Path.Combine(packFolder, packInfoFileName);
        
        var packDetails = new Pack(name, description, new InstalledMods()
        {
            Mods = _mods.Mods,
            NotInModlinksMods = _mods.NotInModlinksMods
        });
        
        await using Stream fs = _fs.File.Exists(packInfo)
            ? _fs.FileStream.New(packInfo, FileMode.Truncate)
            : _fs.File.Create(packInfo);

        await JsonSerializer.SerializeAsync(fs, packDetails, new JsonSerializerOptions() 
        { 
            WriteIndented = true
        });
        
        var packInList = _packList.FirstOrDefault(x => x.Name == name);
        if (packInList != null)
            packInList.InstalledMods = packDetails.InstalledMods;
        else
            _packList.Add(packDetails);
        
        _packList.SortBy((pack1, pack2) => string.CompareOrdinal(pack1.Name, pack2.Name));
    }

    /// <summary>
    /// Remove a profile by object.
    /// </summary>
    /// <param name="packName">The pack to remove.</param>
    /// <exception cref="ArgumentException">The profile to remove was not found in the profile list.</exception>
    public void RemovePack(string packName)
    {
        var pack = _packList.FirstOrDefault(x => x.Name == packName);
        if (pack != null)
        {
            _packList.Remove(pack);
        }
        if (_fs.Directory.Exists(Path.Combine(_settings.ManagedFolder, packName)))
        {
            _fs.Directory.Delete(Path.Combine(_settings.ManagedFolder, packName), true);
        }
    }
    
        /// <summary>
    /// Adds a mods full dependency tree to the dependency list.
    /// </summary>
    private void AddDependency(ISet<string> fullDependencyList, string depName)
    {
        // so we don't unnecessarily go through the dependency list
        if (fullDependencyList.Contains(depName)) return;
                        
        fullDependencyList.Add(depName);
                        
        // add all of its dependency to the dependency list
        if (_items.TryGetValue(depName, out var depMod))
        {
            foreach (var dep in depMod.Dependencies)
            {
                AddDependency(fullDependencyList, dep);
            }
        }
    }
    
    /// <summary>
    /// Helper function to copy a directory.
    /// From: https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-copy-directories
    /// </summary>
    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        // Get information about the source directory
        var dir = new DirectoryInfo(sourceDir);

        // Check if the source directory exists
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        // Cache directories before we start copying
        DirectoryInfo[] dirs = dir.GetDirectories();

        // Create the destination directory
        Directory.CreateDirectory(destinationDir);

        // Get the files in the source directory and copy to the destination directory
        foreach (FileInfo file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath);
        }

        // copy subdirectories by recursively call this method

        foreach (DirectoryInfo subDir in dirs)
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
        
    }

    private async Task EnsureGameClosed()
    {
        static bool IsHollowKnight(Process p) => (
            p.ProcessName.StartsWith("hollow_knight")
            || p.ProcessName.StartsWith("Hollow Knight")
        );
            
        if (Process.GetProcesses().FirstOrDefault(IsHollowKnight) is { } proc)
        {
            var res = await MessageBoxUtil.GetMessageBoxStandardWindow(new MessageBoxStandardParams {
                ContentTitle = Resources.MLVM_InternalUpdateInstallAsync_Msgbox_W_Title,
                ContentMessage = Resources.MLVM_InternalUpdateInstallAsync_Msgbox_W_Text,
                ButtonDefinitions = ButtonEnum.YesNo,
                MinHeight = 200,
                SizeToContent = SizeToContent.WidthAndHeight,
            }).Show();

            if (res == ButtonResult.Yes)
                proc.Kill();
        }
    }
}