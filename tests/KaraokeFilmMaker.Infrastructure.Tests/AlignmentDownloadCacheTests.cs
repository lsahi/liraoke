using System.Security.Cryptography;
using KaraokeFilmMaker.Infrastructure.Alignment;

namespace KaraokeFilmMaker.Infrastructure.Tests;
public sealed class AlignmentDownloadCacheTests
{
    [Fact] public async Task OnlyPinnedBytesAreImportedAndExistingCacheIsRevalidated()
    {
        string root=Path.Combine(Path.GetTempPath(),"alignment-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            byte[] bytes=[1,2,3,4];string hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string source=Path.Combine(root,"renamed.whl"),bad=Path.Combine(root,"bad.whl"),cache=Path.Combine(root,"cache");
            await File.WriteAllBytesAsync(source,bytes);await File.WriteAllBytesAsync(bad,[4,3,2,1]);
            var store=new AlignmentDownloadCache(cache,[new(hash,bytes.Length)]);
            Assert.Equal(new AlignmentCacheImportResult(1,0,1),await store.ImportAsync([source,bad],null,default));
            Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(cache,hash)));
            Assert.Equal(new AlignmentCacheImportResult(0,1,0),await store.ImportAsync([source],null,default));
            await File.WriteAllBytesAsync(Path.Combine(cache,hash),[4,3,2,1]);
            Assert.Equal(new AlignmentCacheImportResult(1,0,0),await store.ImportAsync([source],null,default));
            Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(cache,hash)));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task CancelAndExistingInstallLockPreventCacheMutation()
    {
        string root=Path.Combine(Path.GetTempPath(),"alignment-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var service=new LocalAlignmentService(root);using var cancel=new CancellationTokenSource();cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ImportDownloadsAsync([],null,cancel.Token));
            using var locked=new FileStream(Path.Combine(root,"operation.lock"),FileMode.Create,FileAccess.ReadWrite,FileShare.None);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ImportDownloadsAsync([],null,default));
            Assert.False(Directory.Exists(Path.Combine(root,"cache")));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void CatalogIncludesNestedRuntimeAndModelFiles()
    {
        var result=AlignmentDownloadCache.ReadCatalog("""{"torch":{"gpu":{"sha256":"one","bytes":20}},"qwen":{"files":[{"sha256":"two","bytes":30}]}}""");
        Assert.Equal([new("one",20),new AlignmentDownloadSpec("two",30)],result);
    }
}
