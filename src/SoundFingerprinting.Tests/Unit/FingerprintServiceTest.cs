﻿namespace SoundFingerprinting.Tests.Unit
{
    using System.Collections.Generic;
    using System.Linq;

    using Moq;

    using NUnit.Framework;
    using SoundFingerprinting.Audio;
    using SoundFingerprinting.Configuration;
    using SoundFingerprinting.Data;
    using SoundFingerprinting.FFT;
    using SoundFingerprinting.LSH;
    using SoundFingerprinting.Utils;
    using SoundFingerprinting.Wavelets;

    [TestFixture]
    public class FingerprintServiceTest : AbstractTest
    {
        private FingerprintService fingerprintService;
        private Mock<IFingerprintDescriptor> fingerprintDescriptor;
        private Mock<ISpectrumService> spectrumService;
        private Mock<IWaveletDecomposition> waveletDecomposition;
        private Mock<ILocalitySensitiveHashingAlgorithm> localitySensitiveHashingAlgorithm;

        [SetUp]
        public void SetUp()
        {
            fingerprintDescriptor = new Mock<IFingerprintDescriptor>(MockBehavior.Strict);
            spectrumService = new Mock<ISpectrumService>(MockBehavior.Strict);
            waveletDecomposition = new Mock<IWaveletDecomposition>(MockBehavior.Strict);
            localitySensitiveHashingAlgorithm = new Mock<ILocalitySensitiveHashingAlgorithm>(MockBehavior.Strict);
            fingerprintService = new FingerprintService(
                spectrumService.Object,
                localitySensitiveHashingAlgorithm.Object,
                waveletDecomposition.Object,
                fingerprintDescriptor.Object);
        }

        [TearDown]
        public void TearDown()
        {
            fingerprintDescriptor.VerifyAll();
            spectrumService.VerifyAll();
            waveletDecomposition.VerifyAll();
        }

        [Test]
        public void CreateFingerprints()
        {
            const int tenSeconds = 5512 * 10;
            var samples = TestUtilities.GenerateRandomAudioSamples(tenSeconds);
            var fingerprintConfig = new DefaultFingerprintConfiguration();
            var dividedLogSpectrum = GetDividedLogSpectrum();
            spectrumService.Setup(service => service.CreateLogSpectrogram(samples, It.IsAny<DefaultSpectrogramConfig>())).Returns(dividedLogSpectrum);
            waveletDecomposition.Setup(service => service.DecomposeImageInPlace(It.IsAny<float[]>(), 128, 32, fingerprintConfig.HaarWaveletNorm));
            fingerprintDescriptor.Setup(descriptor => descriptor.ExtractTopWavelets(It.IsAny<float[]>(), fingerprintConfig.TopWavelets, It.IsAny<ushort[]>())).Returns(new TinyFingerprintSchema(8192).SetTrueAt(0, 1));
            localitySensitiveHashingAlgorithm.Setup(service => service.Hash(It.IsAny<Fingerprint>(), fingerprintConfig.HashingConfig))
                .Returns(new HashedFingerprint(new int[0], 1, 0f));

            var fingerprints = fingerprintService.CreateFingerprintsFromAudioSamples(samples, fingerprintConfig)
                                                 .OrderBy(f => f.SequenceNumber)
                                                 .ToList();

            Assert.AreEqual(dividedLogSpectrum.Count, fingerprints.Count);
        }

        [Test]
        public void SilenceIsNotFingerprinted()
        {
            var samples = TestUtilities.GenerateRandomAudioSamples(5512 * 10);
            var configuration = new DefaultFingerprintConfiguration();
            var dividedLogSpectrum = GetDividedLogSpectrum();

            spectrumService.Setup(service => service.CreateLogSpectrogram(samples, It.IsAny<DefaultSpectrogramConfig>())).Returns(dividedLogSpectrum);

            waveletDecomposition.Setup(decomposition => decomposition.DecomposeImageInPlace(It.IsAny<float[]>(), 128, 32, configuration.HaarWaveletNorm));
            fingerprintDescriptor.Setup(descriptor => descriptor.ExtractTopWavelets(It.IsAny<float[]>(), configuration.TopWavelets, It.IsAny<ushort[]>())).Returns(
                    new TinyFingerprintSchema(1024));

            var rawFingerprints = fingerprintService.CreateFingerprintsFromAudioSamples(samples, configuration);

            Assert.IsTrue(!rawFingerprints.Any());
        }

        [Test]
        public void ShouldReturnNonNullEntriesForSilence()
        {
            var silence = new float[8192 + 2048];

            var result = FingerprintService.Instance.CreateFingerprintsFromAudioSamples(new AudioSamples(silence, string.Empty, 5512), new DefaultFingerprintConfiguration()).ToList();
            
            Assert.IsTrue(!result.Any());
        }

        [Test]
        public void ShouldCreateOneFingerprint()
        {
            var floats = TestUtilities.GenerateRandomFloatArray(8192 + 2048 - 64);

            var fingerprints = FingerprintService.Instance.CreateFingerprintsFromAudioSamples(new AudioSamples(floats, string.Empty, 5512), new DefaultFingerprintConfiguration()).ToList();

            Assert.IsNotEmpty(fingerprints);
            Assert.AreEqual(1, fingerprints.Count);
        }

        private static List<Frame> GetDividedLogSpectrum()
        {
            var dividedLogSpectrum = new List<Frame>();
            for (uint index = 0; index < 4; index++)
            {
                dividedLogSpectrum.Add(new Frame(TestUtilities.GenerateRandomFloatArray(4096), 128, 32, 0.928f * index, index));
            }

            return dividedLogSpectrum;
        }
    }
}
