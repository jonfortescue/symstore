﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.FileFormats.MachO
{
    public class MachCore
    {
        private readonly MachOFile _machO;
        private readonly ulong _dylinkerHintAddress;
        private readonly Lazy<ulong> _dylinkerAddress;
        private readonly Lazy<MachDyld> _dylinker;
        private readonly Lazy<MachLoadedImage[]> _loadedImages;

        public MachCore(IAddressSpace dataSource, ulong dylinkerHintAddress = 0)
        {
            _machO = new MachOFile(dataSource);
            _dylinkerHintAddress = dylinkerHintAddress;
            _dylinkerAddress = new Lazy<ulong>(FindDylinker);
            _dylinker = new Lazy<MachDyld>(() => new MachDyld(new MachOFile(VirtualAddressReader.DataSource, DylinkerAddress, true)));
            _loadedImages = new Lazy<MachLoadedImage[]>(ReadImages);
        }

        public Reader VirtualAddressReader { get { return _machO.VirtualAddressReader; } }
        public ulong DylinkerAddress { get { return _dylinkerAddress.Value; } }
        public MachDyld Dylinker { get { return _dylinker.Value; } }
        public IEnumerable<MachLoadedImage> LoadedImages { get { return _loadedImages.Value; } }

        public bool IsValid()
        {
            return _machO.IsValid() && _machO.Header.FileType == MachHeaderFileType.Core;
        }

        private ulong FindDylinker()
        {
            if (_dylinkerHintAddress != 0 && IsValidDylinkerAddress(_dylinkerHintAddress))
            {
                return _dylinkerHintAddress;
            }
            foreach (MachSegment segment in _machO.Segments)
            {
                ulong position = segment.LoadCommand.VMAddress;
                for (ulong offset = 0; offset < segment.LoadCommand.FileSize; offset += 0x1000)
                {
                    if (IsValidDylinkerAddress(position + offset))
                    {
                        return position + offset;
                    }
                }
            }
            throw new BadInputFormatException("No dylinker module found");
        }

        private bool IsValidDylinkerAddress(ulong possibleDylinkerAddress)
        {
            MachOFile dylinker = new MachOFile(VirtualAddressReader.DataSource, possibleDylinkerAddress, true);
            return dylinker.HeaderMagic.IsMagicValid.Check() &&
                   dylinker.Header.FileType == MachHeaderFileType.Dylinker;
        }

        private MachLoadedImage[] ReadImages()
        {
            return Dylinker.Images.Select(i => new MachLoadedImage(new MachOFile(VirtualAddressReader.DataSource, i.LoadAddress, true), i)).ToArray();
        }
    }

    public class MachLoadedImage
    {
        private readonly DyldLoadedImage _dyldLoadedImage;

        public MachLoadedImage(MachOFile image, DyldLoadedImage dyldLoadedImage)
        {
            Image = image;
            _dyldLoadedImage = dyldLoadedImage;
        }

        public MachOFile Image { get; private set; }
        public ulong LoadAddress { get { return _dyldLoadedImage.LoadAddress; } }
        public string Path { get { return _dyldLoadedImage.Path; } }
    }

    public class MachDyld
    {
        private readonly MachOFile _dyldImage;
        private readonly Lazy<ulong> _dyldAllImageInfosAddress;
        private readonly Lazy<DyldImageAllInfosV2> _dyldAllImageInfos;
        private readonly Lazy<DyldImageInfo[]> _imageInfos;
        private readonly Lazy<DyldLoadedImage[]> _images;

        public MachDyld(MachOFile dyldImage)
        {
            _dyldImage = dyldImage;
            _dyldAllImageInfosAddress = new Lazy<ulong>(FindAllImageInfosAddress);
            _dyldAllImageInfos = new Lazy<DyldImageAllInfosV2>(ReadAllImageInfos);
            _imageInfos = new Lazy<DyldImageInfo[]>(ReadImageInfos);
            _images = new Lazy<DyldLoadedImage[]>(ReadLoadedImages);
        }

        public ulong AllImageInfosAddress { get { return _dyldAllImageInfosAddress.Value; } }
        public DyldImageAllInfosV2 AllImageInfos { get { return _dyldAllImageInfos.Value; } }
        public IEnumerable<DyldImageInfo> ImageInfos { get { return _imageInfos.Value; } }
        public IEnumerable<DyldLoadedImage> Images { get { return _images.Value; } }

        private ulong FindAllImageInfosAddress()
        {
            // The symbol may be decorated so check if it contains the loader symbol instead of comparing it exactly
            ulong preferredAddress = _dyldImage.Symtab.Symbols.Where(s => s.Name.Contains("dyld_all_image_infos")).First().Value;
            return preferredAddress - _dyldImage.PreferredVMBaseAddress + _dyldImage.LoadAddress;
        }

        private DyldImageAllInfosV2 ReadAllImageInfos()
        {
            DyldImageAllInfosVersion version = _dyldImage.VirtualAddressReader.Read<DyldImageAllInfosVersion>(AllImageInfosAddress);
            return _dyldImage.VirtualAddressReader.Read<DyldImageAllInfosV2>(AllImageInfosAddress);
        }

        private DyldImageInfo[] ReadImageInfos()
        {
            return _dyldImage.VirtualAddressReader.ReadArray<DyldImageInfo>(AllImageInfos.InfoArray, AllImageInfos.InfoArrayCount);
        }

        private DyldLoadedImage[] ReadLoadedImages()
        {
            return ImageInfos.Select(i => new DyldLoadedImage(_dyldImage.VirtualAddressReader.Read<string>(i.PathAddress), i)).ToArray();
        }
    }

    public class DyldLoadedImage
    {
        private readonly DyldImageInfo _imageInfo;

        public DyldLoadedImage(string path, DyldImageInfo imageInfo)
        {
            Path = path;
            _imageInfo = imageInfo;
        }

        public string Path;
        public ulong LoadAddress { get { return _imageInfo.Address; } }
    }
}
