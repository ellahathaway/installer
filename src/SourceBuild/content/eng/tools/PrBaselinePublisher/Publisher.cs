﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace PrBaselinePublisher;

using Octokit;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

public class Publisher
{
    private string _repoOwner;
    private string _repoName;
    private GitHubClient _client;

    public Publisher(string repo, string gitHubToken)
    {
        // Create a new GitHub client
        _client = new GitHubClient(new ProductHeaderValue(System.Reflection.Assembly.GetExecutingAssembly().GetName().Name));
        var authToken = new Credentials(gitHubToken);
        _client.Credentials = authToken;
        _repoOwner = repo.Split('/')[0];
        _repoName = repo.Split('/')[1];
    }

    private static readonly string DefaultLicenseBaselineContent = "{\n  \"files\": []\n}";
    private record ChangedFile(string Path, string Content);

    public async Task<int> ExecuteAsync(
        string originalTestResultsPath,
        string updatedTestsResultsPath,
        int buildId,
        string title,
        string targetBranch,
        Pipelines pipeline)
    {
        DateTime startTime = DateTime.Now;

        Log.LogInformation($"Starting PR baseline publisher at {startTime} for pipeline {pipeline}.");

        var updatedTestsFiles = GetUpdatedFiles(updatedTestsResultsPath);

        // Create a new tree for the originalTestResultsPath based on the target branch
        var testResultsTreeItems = new List<NewTreeItem>();
        var originalTreeResponse = await _client.Git.Tree.GetRecursive(_repoOwner, _repoName, targetBranch);
        foreach (var file in originalTreeResponse.Tree)
        {
            if (file.Path.Contains(originalTestResultsPath) && file.Path != originalTestResultsPath)
            {
                testResultsTreeItems.Add(new NewTreeItem
                {
                    Path = Path.GetRelativePath(originalTestResultsPath, file.Path),
                    Mode = file.Mode,
                    Type = file.Type.Value,
                    Sha = file.Sha
                });
            }
        }

        // Update the test results tree based on the pipeline
        if (pipeline == Pipelines.Sdk)
        {
            testResultsTreeItems = await UpdateSdkDiffFilesAsync(updatedTestsFiles, testResultsTreeItems);
        }
        else if (pipeline == Pipelines.License)
        {
            testResultsTreeItems = await UpdateLicenseScanFilesAsync(updatedTestsFiles, testResultsTreeItems);
        }
        else
        {
            Log.LogError("Invalid pipeline.");
        }

        var testResultsTreeResponse = await CreateTreeFromItems(testResultsTreeItems);
        var parentTreeResponse = await CreateParentTree(testResultsTreeResponse, originalTreeResponse, originalTestResultsPath);

        await CreateOrUpdatePullRequest(parentTreeResponse, buildId, title, targetBranch);

        return Log.GetExitCode();
    }

    private Dictionary<string, HashSet<string>> GetUpdatedFiles(string updatedTestsResultsPath)
    {
        // Store in a dictionary using the filename without the 
        // "Updated" prefix and anything after the first '.' as the key
        Dictionary<string, HashSet<string>> updatedFiles = new();

        var updatedTestsFiles = Directory.GetFiles(updatedTestsResultsPath, "Updated*", SearchOption.AllDirectories);
        foreach (string updatedTestsFile in updatedTestsFiles)
        {
            string updatedFileKey = ParseUpdatedFileName(updatedTestsFile).Split('.')[0];
            if (!updatedFiles.ContainsKey(updatedFileKey))
            {
                updatedFiles[updatedFileKey] = new HashSet<string>();
            }
            updatedFiles[updatedFileKey].Add(updatedTestsFile);
        }
        return updatedFiles;
    }

    private async Task<List<NewTreeItem>> UpdateLicenseScanFilesAsync(Dictionary<string, HashSet<string>> updatedFiles, List<NewTreeItem> tree)
    {
        foreach (var updatedFile in updatedFiles)
        {
            if (updatedFile.Key.Contains("Exclusions"))
            {
                // Combine the exclusions files
                IEnumerable<string> parsedFile = Enumerable.Empty<string>();
                foreach (var filePath in updatedFile.Value)
                {
                    var updatedFileLines = File.ReadAllLines(filePath);
                    parsedFile = parsedFile.Any() ? parsedFile.Where(parsedLine => updatedFileLines.Contains(parsedLine)) : updatedFileLines;
                }
                string? content = parsedFile.Any() ? string.Join("\n", parsedFile) : null;
                string updatedFilePath = updatedFile.Key + ".txt";
                tree = await UpdateFile(tree, content, updatedFile.Key, updatedFilePath);
            }
            else
            {
                // Update the other files
                foreach (var filePath in updatedFile.Value)
                {
                    var content = File.ReadAllText(filePath);
                    if (content == DefaultLicenseBaselineContent)
                    {
                        content = null;
                    }
                    string originalFileName = Path.GetFileName(ParseUpdatedFileName(filePath));
                    string updatedFilePath = Path.Combine("baselines/licenses", originalFileName);
                    tree = await UpdateFile(tree, content, originalFileName, updatedFilePath);
                }
            }
        }
        return tree;
    }

    private async Task<List<NewTreeItem>> UpdateSdkDiffFilesAsync(Dictionary<string, HashSet<string>> updatedFiles, List<NewTreeItem> tree)
    {
        foreach (var updatedFile in updatedFiles)
        {
            if (updatedFile.Key.Contains("Exclusions"))
            {
                // Combine the exclusions files
                IEnumerable<string> parsedFile = Enumerable.Empty<string>();
                foreach (var filePath in updatedFile.Value)
                {
                    var updatedFileLines = File.ReadAllLines(filePath);
                    parsedFile = parsedFile.Any() ? parsedFile.Union(updatedFileLines) : updatedFileLines;
                }
                // Find the key in the tree, download the blob, and convert it to utf8
                var originalTreeItem = tree
                    .Where(item => item.Path.Contains(updatedFile.Key))
                    .FirstOrDefault();

                string? content = null;
                if (originalTreeItem != null)
                {
                    var originalBlob = await _client.Git.Blob.Get(_repoOwner, _repoName, originalTreeItem.Sha);
                    content = Encoding.UTF8.GetString(Convert.FromBase64String(originalBlob.Content));
                    var originalContent = content.Split("\n");

                    foreach (var line in originalContent)
                    {
                        if (!parsedFile.Contains(line))
                        {
                            content = content.Replace(line + "\n", "");
                        }
                    }
                }
                else
                {
                    content = parsedFile.Any() ? string.Join("\n", parsedFile) : null;
                }
                string updatedFilePath = updatedFile.Key + ".txt";
                tree = await UpdateFile(tree, content, updatedFile.Key, updatedFilePath);
            }
            else
            {
                // Update the other files
                foreach (var filePath in updatedFile.Value)
                {
                    var content = File.ReadAllText(filePath);
                    string originalFileName = Path.GetFileName(ParseUpdatedFileName(filePath));
                    string updatedFilePath = Path.Combine("baselines", originalFileName);
                    tree = await UpdateFile(tree, content, originalFileName, updatedFilePath);
                }
            }
        }
        return tree;
    }

    private async Task<List<NewTreeItem>> UpdateFile(List<NewTreeItem> tree, string? content, string searchFileName, string updatedPath)
    {
        var originalTreeItem = tree
            .Where(item => item.Path.Contains(searchFileName))
            .FirstOrDefault();

        if (content == null)
        {
            // Content is null, delete the file if it exists
            if (originalTreeItem != null)
            {
                tree.Remove(originalTreeItem);
            }
        }
        else if (originalTreeItem == null)
        {
            // Path not in the tree, add a new tree item
            var blob = await CreateBlob(content);
            tree.Add(new NewTreeItem
            {
                Type = TreeType.Blob,
                Mode = FileMode.File,
                Path = updatedPath,
                Sha = blob.Sha
            });
        }
        else
        {
            // Path in the tree, update the sha and the content
            var blob = await CreateBlob(content);
            originalTreeItem.Sha = blob.Sha;
        }
        return tree;
    }

    private async Task<BlobReference> CreateBlob(string content)
    {
        var blob = new NewBlob
        {
            Content = content,
            Encoding = EncodingType.Utf8
        };
        return await _client.Git.Blob.Create(_repoOwner, _repoName, blob);
    }

    private string ParseUpdatedFileName(string updatedFile)
    {
        return updatedFile.Split("Updated")[1];
    }

    private async Task<TreeResponse> CreateTreeFromItems(List<NewTreeItem> items, string path = "")
    {
        var newTreeItems = new List<NewTreeItem>();

        var groups = items.GroupBy(item => Path.GetDirectoryName(item.Path));
        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.Key) || group.Key == path)
            {
                // These items are in the current directory, so add them to the new tree items
                foreach (var item in group)
                {
                    if(item.Type != TreeType.Tree)
                    {
                        newTreeItems.Add(new NewTreeItem
                        {
                            Path = path == string.Empty ? item.Path : Path.GetRelativePath(path, item.Path),
                            Mode = item.Mode,
                            Type = item.Type,
                            Sha = item.Sha
                        });
                    }
                }
            }
            else
            {
                // These items are in a subdirectory, so recursively create a tree for them
                var subtreeResponse = await CreateTreeFromItems(group.ToList(), group.Key);
                newTreeItems.Add(new NewTreeItem
                {
                    Path = group.Key,
                    Mode = "040000",
                    Type = TreeType.Tree,
                    Sha = subtreeResponse.Sha
                });
            }
        }

        var newTree = new NewTree();
        foreach (var item in newTreeItems)
        {
            newTree.Tree.Add(item);
        }
        return await _client.Git.Tree.Create(_repoOwner, _repoName, newTree);
    }

    private async Task<TreeResponse> CreateParentTree(TreeResponse testResultsTreeResponse, TreeResponse originalTreeResponse, string originalTestResultsPath)
    {
        // Create a new tree for the parent directory
        // excluding anything in the updated test results tree
        NewTree parentTree = new NewTree();
        foreach (var file in originalTreeResponse.Tree)
        {
            if (!file.Path.Contains(originalTestResultsPath))
            {
                parentTree.Tree.Add(new NewTreeItem
                {
                    Path = file.Path,
                    Mode = file.Mode,
                    Type = file.Type.Value,
                    Sha = file.Sha
                });
            }
        }

        //  Connect the updated test results tree
        parentTree.Tree.Add(new NewTreeItem
        {
            Path = originalTestResultsPath,
            Mode = "040000",
            Type = TreeType.Tree,
            Sha = testResultsTreeResponse.Sha
        });

        return await _client.Git.Tree.Create(_repoOwner, _repoName, parentTree);
    }

    private async Task CreateOrUpdatePullRequest(TreeResponse parentTreeResponse, int buildId, string title, string targetBranch)
    {
        // Look for a pre-existing pull request
        var request = new PullRequestRequest
        {
            Base = targetBranch
        };
        var existingPullRequest = await _client.PullRequest.GetAllForRepository(_repoOwner, _repoName, request);
        var matchingPullRequest = existingPullRequest.FirstOrDefault(pr => pr.Title == title);

        // Create the branch name and get the head reference
        string newBranchName = string.Empty;
        Reference? headReference = null;
        if (matchingPullRequest == null)
        {
            string utcTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            newBranchName = $"pr-baseline-{utcTime}";
            headReference = await _client.Git.Reference.Get(_repoOwner, _repoName, "heads/" + targetBranch);
        }
        else
        {
            newBranchName = matchingPullRequest.Head.Ref;
            headReference = await _client.Git.Reference.Get(_repoOwner, _repoName, "heads/" + matchingPullRequest.Head.Ref);
        }

        // Create the commit
        string commitMessage = $"Update baselines for build https://dev.azure.com/dnceng/internal/_build/results?buildId={buildId}&view=results";
        var newCommit = new NewCommit(commitMessage, parentTreeResponse.Sha, headReference.Object.Sha);
        var commitResponse = await _client.Git.Commit.Create(_repoOwner, _repoName, newCommit);

        if (matchingPullRequest != null)
        {
            // Update the existing pull request
            var referenceUpdate = new ReferenceUpdate(commitResponse.Sha);
            await _client.Git.Reference.Update(_repoOwner, _repoName, $"heads/{newBranchName}", referenceUpdate);

            Log.LogInformation($"Updated existing pull request #{matchingPullRequest.Number}. URL: {matchingPullRequest.HtmlUrl}");
        }
        else
        {
            // Create a new pull request
            var newReference = new NewReference("refs/heads/" + newBranchName, commitResponse.Sha);
            await _client.Git.Reference.Create(_repoOwner, _repoName, newReference);

            var newPullRequest = new NewPullRequest(title, newBranchName, targetBranch)
            {
                Body = $"This PR was created by the PR baseline publisher tool for build {buildId}. \n\n" +
                       $"The updated test results can be found at https://dev.azure.com/dnceng/internal/_build/results?buildId={buildId}&view=results",
            };
            var pullRequest = await _client.PullRequest.Create(_repoOwner, _repoName, newPullRequest);

            Log.LogInformation($"Created pull request #{pullRequest.Number}. URL: {pullRequest.HtmlUrl}");
        }
    }
}