﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace PrBaselinePublisher;

using Octokit;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

public class Publisher
{
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly GitHubClient _client;
    private const string BuildLink = "https://dev.azure.com/dnceng/internal/_build/results?buildId=";
    private const string TreeMode = "040000";

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

    public async Task<int> ExecuteAsync(
        string originalTestResultsPath,
        string updatedTestsResultsPath,
        int buildId,
        string title,
        string targetBranch,
        Pipelines pipeline)
    {
        DateTime startTime = DateTime.Now.ToUniversalTime();

        Log.LogInformation($"Starting PR baseline publisher at {startTime} UTC for pipeline {pipeline}.");

        var updatedTestsFiles = GetUpdatedFiles(updatedTestsResultsPath);

        // Create a new tree for the originalTestResultsPath based on the target branch
        var originalTreeResponse = await _client.Git.Tree.GetRecursive(_repoOwner, _repoName, targetBranch);
        var testResultsTreeItems = originalTreeResponse.Tree
            .Where(file => file.Path.Contains(originalTestResultsPath) && file.Path != originalTestResultsPath)
            .Select(file => new NewTreeItem
            {
                Path = Path.GetRelativePath(originalTestResultsPath, file.Path),
                Mode = file.Mode,
                Type = file.Type.Value,
                Sha = file.Sha
            })
            .ToList();

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
            string errorMessage = "Invalid pipeline.";
            Log.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        var testResultsTreeResponse = await CreateTreeFromItemsAsync(testResultsTreeItems);
        var parentTreeResponse = await CreateParentTreeAsync(testResultsTreeResponse, originalTreeResponse, originalTestResultsPath);

        await CreateOrUpdatePullRequestAsync(parentTreeResponse, buildId, title, targetBranch);

        return Log.GetExitCode();
    }

    // Return a dictionary using the filename without the 
    // "Updated" prefix and anything after the first '.' as the key
    private Dictionary<string, HashSet<string>> GetUpdatedFiles(string updatedTestsResultsPath) =>
        Directory
            .GetFiles(updatedTestsResultsPath, "Updated*", SearchOption.AllDirectories)
            .GroupBy(updatedTestsFile => ParseUpdatedFileName(updatedTestsFile).Split('.')[0])
            .ToDictionary(
                group => group.Key,
                group => new HashSet<string>(group)
            );

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
                string? content = parsedFile.Any() ? string.Join("\n", parsedFile) + "\n" : null;
                string updatedFilePath = updatedFile.Key + ".txt";
                tree = await UpdateFileAsync(tree, content, updatedFile.Key, updatedFilePath);
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
                    tree = await UpdateFileAsync(tree, content, originalFileName, updatedFilePath);
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
                    content = parsedFile.Any() ? string.Join("\n", parsedFile) + "\n" : null;
                }
                string updatedFilePath = updatedFile.Key + ".txt";
                tree = await UpdateFileAsync(tree, content, updatedFile.Key, updatedFilePath);
            }
            else
            {
                // Update the other files
                foreach (var filePath in updatedFile.Value)
                {
                    var content = File.ReadAllText(filePath);
                    string originalFileName = Path.GetFileName(ParseUpdatedFileName(filePath));
                    string updatedFilePath = Path.Combine("baselines", originalFileName);
                    tree = await UpdateFileAsync(tree, content, originalFileName, updatedFilePath);
                }
            }
        }
        return tree;
    }

    private async Task<List<NewTreeItem>> UpdateFileAsync(List<NewTreeItem> tree, string? content, string searchFileName, string updatedPath)
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
            var blob = await CreateBlobAsync(content);
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
            var blob = await CreateBlobAsync(content);
            originalTreeItem.Sha = blob.Sha;
        }
        return tree;
    }

    private async Task<BlobReference> CreateBlobAsync(string content)
    {
        var blob = new NewBlob
        {
            Content = content,
            Encoding = EncodingType.Utf8
        };
        return await _client.Git.Blob.Create(_repoOwner, _repoName, blob);
    }

    private string ParseUpdatedFileName(string updatedFile) => updatedFile.Split("Updated")[1];

    private async Task<TreeResponse> CreateTreeFromItemsAsync(List<NewTreeItem> items, string path = "")
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
                var subtreeResponse = await CreateTreeFromItemsAsync(group.ToList(), group.Key);
                newTreeItems.Add(new NewTreeItem
                {
                    Path = group.Key,
                    Mode = TreeMode,
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

    private async Task<TreeResponse> CreateParentTreeAsync(TreeResponse testResultsTreeResponse, TreeResponse originalTreeResponse, string originalTestResultsPath)
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
            Mode = TreeMode,
            Type = TreeType.Tree,
            Sha = testResultsTreeResponse.Sha
        });

        return await _client.Git.Tree.Create(_repoOwner, _repoName, parentTree);
    }

    private async Task CreateOrUpdatePullRequestAsync(TreeResponse parentTreeResponse, int buildId, string title, string targetBranch)
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
        string commitMessage = $"Update baselines for build {BuildLink}{buildId} (internal Microsoft link)";
        var newCommit = new NewCommit(commitMessage, parentTreeResponse.Sha, headReference.Object.Sha);
        var commitResponse = await _client.Git.Commit.Create(_repoOwner, _repoName, newCommit);

        string pullRequestBody = $"This PR was created by the PR baseline publisher tool for build {buildId}. \n\n" +
                                 $"The updated test results can be found at {BuildLink}{buildId} (internal Microsoft link)";
        if (matchingPullRequest != null)
        {
            // Update the existing pull request with the new commit
            var referenceUpdate = new ReferenceUpdate(commitResponse.Sha);
            await _client.Git.Reference.Update(_repoOwner, _repoName, $"heads/{newBranchName}", referenceUpdate);

            // Update the body of the pull request
            var pullRequestUpdate = new PullRequestUpdate
            {
                Body = pullRequestBody
            };
            await _client.PullRequest.Update(_repoOwner, _repoName, matchingPullRequest.Number, pullRequestUpdate);

            Log.LogInformation($"Updated existing pull request #{matchingPullRequest.Number}. URL: {matchingPullRequest.HtmlUrl}");
        }
        else
        {
            var comparison = await _client.Repository.Commit.Compare(_repoOwner, _repoName, headReference.Object.Sha, commitResponse.Sha);
            if (!comparison.Files.Any())
            {
                // No changes to commit
                Log.LogInformation("No changes to commit. Skipping PR creation.");
                return;
            }

            // Create a new pull request
            var newReference = new NewReference("refs/heads/" + newBranchName, commitResponse.Sha);
            await _client.Git.Reference.Create(_repoOwner, _repoName, newReference);

            var newPullRequest = new NewPullRequest(title, newBranchName, targetBranch)
            {
                Body = pullRequestBody
            };
            var pullRequest = await _client.PullRequest.Create(_repoOwner, _repoName, newPullRequest);

            Log.LogInformation($"Created pull request #{pullRequest.Number}. URL: {pullRequest.HtmlUrl}");
        }
    }
}