﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

using CurlToCSharp.Extensions;
using CurlToCSharp.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Net.Http.Headers;

namespace CurlToCSharp.Services
{
    public class ConverterService : IConverterService
    {
        private const string RequestVariableName = "request";

        private const string HttpClientVariableName = "httpClient";

        private const string Base64AuthorizationVariableName = "base64authorization";

        private const string HandlerVariableName = "handler";

        private const string RequestContentPropertyName = "Content";

        public ConvertResult<string> ToCsharp(CurlOptions curlOptions)
        {
            var compilationUnit = SyntaxFactory.CompilationUnit();

            var result = new ConvertResult<string>();
            if (ShouldGenerateHandler(curlOptions))
            {
                var configureHandlerStatements = ConfigureHandlerStatements(curlOptions, result);
                compilationUnit = compilationUnit.AddMembers(configureHandlerStatements.ToArray());
            }

            var httpClientUsing = CreateHttpClientUsing(curlOptions);
            var requestUsingStatements = CreateRequestUsingStatements(curlOptions);

            httpClientUsing = httpClientUsing.WithStatement(SyntaxFactory.Block(requestUsingStatements));
            result.Data = compilationUnit.AddMembers(SyntaxFactory.GlobalStatement(httpClientUsing))
                .NormalizeWhitespace()
                .ToFullString();

            return result;
        }

        private bool IsSupportedProxy(Uri proxyUri)
        {
            if (Uri.UriSchemeHttp == proxyUri.Scheme || Uri.UriSchemeHttps == proxyUri.Scheme)
            {
                return true;
            }

            return false;
        }

        private bool ShouldGenerateHandler(CurlOptions curlOptions)
        {
            return curlOptions.HasCookies || (curlOptions.HasProxy && IsSupportedProxy(curlOptions.ProxyUri));
        }

        /// <summary>
        /// Generates the string content assignment statement.
        /// </summary>
        /// <param name="curlOptions">The curl options.</param>
        /// <returns><see cref="EmptyStatementSyntax"/> statement.</returns>
        /// <remarks>
        /// request.Content = new StringContent("{\"status\": \"resolved\"}", Encoding.UTF8, "application/json");
        /// </remarks>
        private ExpressionStatementSyntax CreateStringContentAssignmentStatement(CurlOptions curlOptions)
        {
            var stringContentCreation = CreateStringContentCreation(curlOptions);

            return SyntaxFactory.ExpressionStatement(
                RoslynExtensions.CreateMemberAssignmentExpression(
                    RequestVariableName,
                    RequestContentPropertyName,
                    stringContentCreation))
                .AppendWhiteSpace();
        }

        /// <summary>
        /// Generates the multipart content statements.
        /// </summary>
        /// <param name="curlOptions">The curl options.</param>
        /// <returns>Collection of <see cref="StatementSyntax"/>.</returns>
        /// <remarks>
        /// var multipartContent = new MultipartContent();
        /// multipartContent.Add(new StringContent("test", Encoding.UTF8, "application/x-www-form-urlencoded"));
        /// multipartContent.Add(new ByteArrayContent(File.ReadAllBytes("file1.txt")));
        /// request.Content = multipartContent;
        /// </remarks>
        private IEnumerable<StatementSyntax> CreateMultipartContentStatements(CurlOptions curlOptions)
        {
            var statements = new LinkedList<StatementSyntax>();

            const string MultipartVariableName = "multipartContent";
            const string MultipartAddMethodName = "Add";

            statements.AddLast(
                SyntaxFactory.LocalDeclarationStatement(
                    RoslynExtensions.CreateVariableFromNewObjectExpression(
                        MultipartVariableName,
                        nameof(MultipartContent))));

            if (!string.IsNullOrEmpty(curlOptions.Payload))
            {
                var stringContentCreation = CreateStringContentCreation(curlOptions);
                var addStatement = SyntaxFactory.ExpressionStatement(
                    RoslynExtensions.CreateInvocationExpression(
                        MultipartVariableName,
                        MultipartAddMethodName,
                        SyntaxFactory.Argument(stringContentCreation)));
                statements.AddLast(addStatement.AppendWhiteSpace());
            }

            foreach (var file in curlOptions.DataFiles)
            {
                var byteArrayContentExpression = CreateNewByteArrayContentExpression(file);

                var addStatement = SyntaxFactory.ExpressionStatement(
                    RoslynExtensions.CreateInvocationExpression(
                        MultipartVariableName,
                        MultipartAddMethodName,
                        SyntaxFactory.Argument(byteArrayContentExpression)));

                statements.AddLast(addStatement);
            }

            statements.AddLast(SyntaxFactory.ExpressionStatement(
                RoslynExtensions.CreateMemberAssignmentExpression(
                    RequestVariableName,
                    RequestContentPropertyName,
                    SyntaxFactory.IdentifierName(MultipartVariableName))));

            statements.TryAppendWhiteSpaceAtEnd();

            return statements;
        }

        /// <summary>
        /// Generates the new byte array content expression.
        /// </summary>
        /// <param name="fileName">The file name.</param>
        /// <returns><see cref="ObjectCreationExpressionSyntax"/> expression.</returns>
        /// <remarks>
        /// new ByteArrayContent(File.ReadAllBytes("file1.txt"))
        /// </remarks>
        private ObjectCreationExpressionSyntax CreateNewByteArrayContentExpression(string fileName)
        {
            var fileReadExpression = RoslynExtensions.CreateInvocationExpression(
                "File",
                "ReadAllBytes",
                RoslynExtensions.CreateStringLiteralArgument(fileName));

            var byteArrayContentExpression = RoslynExtensions.CreateObjectCreationExpression(
                "ByteArrayContent",
                SyntaxFactory.Argument(fileReadExpression));
            return byteArrayContentExpression;
        }

        /// <summary>
        /// Generates the string content creation expression.
        /// </summary>
        /// <param name="curlOptions">The curl options.</param>
        /// <returns><see cref="ObjectCreationExpressionSyntax"/> expression.</returns>
        /// <remarks>
        /// new StringContent("{\"status\": \"resolved\"}", Encoding.UTF8, "application/json")
        /// </remarks>
        private ObjectCreationExpressionSyntax CreateStringContentCreation(CurlOptions curlOptions)
        {
            var arguments = new LinkedList<ArgumentSyntax>();
            arguments.AddLast(RoslynExtensions.CreateStringLiteralArgument(curlOptions.Payload));

            var contentHeader = curlOptions.Headers.GetCommaSeparatedValues(HeaderNames.ContentType);
            if (contentHeader.Any())
            {
                arguments.AddLast(SyntaxFactory.Argument(RoslynExtensions.CreateMemberAccessExpression("Encoding", "UTF8")));
                arguments.AddLast(RoslynExtensions.CreateStringLiteralArgument(contentHeader.First()));
            }

            var stringContentCreation = RoslynExtensions.CreateObjectCreationExpression("StringContent", arguments.ToArray());
            return stringContentCreation;
        }

        /// <summary>
        /// Generates the header assignment statements.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>Collection of <see cref="StatementSyntax"/></returns>
        /// <remarks>
        /// request.Headers.TryAddWithoutValidation("Accept", "application/json");
        /// request.Headers.TryAddWithoutValidation("User-Agent", "curl/7.60.0");
        /// </remarks>
        private IEnumerable<StatementSyntax> CreateHeaderAssignmentStatements(CurlOptions options)
        {
            if (!options.Headers.Any() && !options.HasCookies)
            {
                return Enumerable.Empty<ExpressionStatementSyntax>();
            }

            var statements = new LinkedList<ExpressionStatementSyntax>();
            foreach (var header in options.Headers)
            {
                if (header.Key == HeaderNames.ContentType)
                {
                    continue;
                }

                var tryAddHeaderStatement = CreateTryAddHeaderStatement(
                    RoslynExtensions.CreateStringLiteralArgument(header.Key),
                    RoslynExtensions.CreateStringLiteralArgument(header.Value));

                statements.AddLast(tryAddHeaderStatement);
            }

            if (options.HasCookies)
            {
                statements.AddLast(
                    CreateTryAddHeaderStatement(
                        RoslynExtensions.CreateStringLiteralArgument("Cookie"),
                        RoslynExtensions.CreateStringLiteralArgument(options.CookieValue)));
            }

            statements.TryAppendWhiteSpaceAtEnd();

            return statements;
        }

        /// <summary>
        /// Generates the basic authorization statements.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>Collection of <see cref="StatementSyntax"/>.</returns>
        /// <remarks>
        /// var base64authorization = Convert.ToBase64String(Encoding.ASCII.GetBytes("username:password"));
        /// request.Headers.TryAddWithoutValidation("Authorization", $"Basic {base64authorization}");
        /// </remarks>
        private IEnumerable<StatementSyntax> CreateBasicAuthorizationStatements(CurlOptions options)
        {
            var authorizationEncodingStatement = CreateBasicAuthorizationEncodingStatement(options);
            var stringStartToken = SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken);

            var interpolatedStringContentSyntaxs = new SyntaxList<InterpolatedStringContentSyntax>()
                .Add(SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken, "Basic ", null, SyntaxTriviaList.Empty)))
                .Add(SyntaxFactory.Interpolation(SyntaxFactory.IdentifierName(Base64AuthorizationVariableName)));

            var interpolatedStringArgument = SyntaxFactory.Argument(SyntaxFactory.InterpolatedStringExpression(stringStartToken, interpolatedStringContentSyntaxs));
            var tryAddHeaderStatement = CreateTryAddHeaderStatement(RoslynExtensions.CreateStringLiteralArgument("Authorization"), interpolatedStringArgument)
                .AppendWhiteSpace();

            return new StatementSyntax[] { authorizationEncodingStatement, tryAddHeaderStatement };
        }

        /// <summary>
        /// Generates the basic authorization encoding statement.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns><see cref="LocalDeclarationStatementSyntax"/> statement.</returns>
        /// <remarks>
        /// var base64authorization = Convert.ToBase64String(Encoding.ASCII.GetBytes("username:password"));
        /// </remarks>
        private LocalDeclarationStatementSyntax CreateBasicAuthorizationEncodingStatement(CurlOptions options)
        {
            var asciiGetBytesInvocation = RoslynExtensions.CreateInvocationExpression(
                "Encoding",
                "ASCII",
                "GetBytes",
                RoslynExtensions.CreateStringLiteralArgument(options.UserPasswordPair));

            var convertToBase64Invocation = RoslynExtensions.CreateInvocationExpression(
                "Convert",
                "ToBase64String",
                SyntaxFactory.Argument(asciiGetBytesInvocation));

            var declarationSyntax = RoslynExtensions.CreateVariableInitializationExpression(
                Base64AuthorizationVariableName,
                convertToBase64Invocation);

            return SyntaxFactory.LocalDeclarationStatement(declarationSyntax);
        }

        /// <summary>
        /// Generates the headers adding statement.
        /// </summary>
        /// <param name="keyArgumentSyntax">The header key argument syntax.</param>
        /// <param name="valueArgumentSyntax">The header value argument syntax.</param>
        /// <returns><see cref="ExpressionStatementSyntax"/> statement.</returns>
        /// <remarks>
        /// request.Headers.TryAddWithoutValidation("Accept", "application/json");
        /// </remarks>
        private ExpressionStatementSyntax CreateTryAddHeaderStatement(ArgumentSyntax keyArgumentSyntax, ArgumentSyntax valueArgumentSyntax)
        {
            var invocationExpressionSyntax = RoslynExtensions.CreateInvocationExpression(
                RequestVariableName,
                "Headers",
                "TryAddWithoutValidation",
                keyArgumentSyntax,
                valueArgumentSyntax);

            return SyntaxFactory.ExpressionStatement(invocationExpressionSyntax);
        }

        /// <summary>
        /// Generate the HttpClient using statements with empty using block.
        /// </summary>
        /// <param name="curlOptions">The curl options.</param>
        /// <returns>Collection of <see cref="UsingStatementSyntax"/>.</returns>
        /// <remarks>
        /// using (var httpClient = new HttpClient())
        /// {
        /// }
        /// </remarks>
        private UsingStatementSyntax CreateHttpClientUsing(CurlOptions curlOptions)
        {
            var argumentSyntax = ShouldGenerateHandler(curlOptions)
                                     ? new[] { SyntaxFactory.Argument(SyntaxFactory.IdentifierName(HandlerVariableName)) }
                                     : new ArgumentSyntax[0];
            return RoslynExtensions.CreateUsingStatement(HttpClientVariableName, nameof(HttpClient), argumentSyntax);
        }

        /// <summary>
        /// Generate the HttpRequestMessage using statements with statements inside the using blocks.
        /// </summary>
        /// <param name="curlOptions">The curl options.</param>
        /// <returns>Collection of <see cref="UsingStatementSyntax"/>.</returns>
        /// <remarks>
        /// using (var request = new HttpRequestMessage(new HttpMethod("GET"), "https://github.com/"))
        /// {
        ///     var response = await httpClient.SendAsync(request);
        /// }
        /// </remarks>
        private IEnumerable<UsingStatementSyntax> CreateRequestUsingStatements(CurlOptions curlOptions)
        {
            var innerBlock = SyntaxFactory.Block();

            var methodNameArgument = RoslynExtensions.CreateStringLiteralArgument(curlOptions.HttpMethod);
            var httpMethodArgument = RoslynExtensions.CreateObjectCreationExpression(nameof(HttpMethod), methodNameArgument);

            var urlArgument = RoslynExtensions.CreateStringLiteralArgument(curlOptions.Url.ToString());
            var requestUsingStatement = RoslynExtensions.CreateUsingStatement(
                RequestVariableName,
                nameof(HttpRequestMessage),
                SyntaxFactory.Argument(httpMethodArgument),
                urlArgument);

            var statements = CreateHeaderAssignmentStatements(curlOptions);
            innerBlock = innerBlock.AddStatements(statements.ToArray());

            if (!string.IsNullOrEmpty(curlOptions.UserPasswordPair))
            {
                var basicAuthorizationStatements = CreateBasicAuthorizationStatements(curlOptions);
                innerBlock = innerBlock.AddStatements(basicAuthorizationStatements.ToArray());
            }

            var requestInnerBlocks = new LinkedList<UsingStatementSyntax>();
            if (curlOptions.DataFiles.Any())
            {
                var multipartContentStatements = CreateMultipartContentStatements(curlOptions);
                requestInnerBlocks.AddLast(
                    requestUsingStatement.WithStatement(
                        innerBlock.AddStatements(multipartContentStatements.ToArray())));
            }
            else if (!string.IsNullOrWhiteSpace(curlOptions.Payload))
            {
                var assignmentExpression = CreateStringContentAssignmentStatement(curlOptions);
                requestInnerBlocks.AddLast(
                    requestUsingStatement.WithStatement(innerBlock.AddStatements(assignmentExpression)));
            }
            else if (curlOptions.UploadFiles.Any())
            {
                foreach (var file in curlOptions.UploadFiles)
                {
                    // NOTE that you must use a trailing / on the last directory to really prove to
                    // Curl that there is no file name or curl will think that your last directory name is the remote file name to use.
                    if (!string.IsNullOrEmpty(curlOptions.Url.PathAndQuery)
                        && curlOptions.Url.PathAndQuery.EndsWith('/'))
                    {
                        var objectCreationExpressionSyntaxs = requestUsingStatement.DescendantNodes()
                            .OfType<ObjectCreationExpressionSyntax>()
                            .First(
                                t => t.Type is IdentifierNameSyntax identifier
                                     && identifier.Identifier.ValueText == nameof(HttpRequestMessage));

                        var s = objectCreationExpressionSyntaxs.ArgumentList.Arguments.Last();

                        requestUsingStatement = requestUsingStatement.ReplaceNode(
                            s,
                            RoslynExtensions.CreateStringLiteralArgument(curlOptions.GetUrlForFileUpload(file).ToString()));
                    }

                    var byteArrayContentExpression = CreateNewByteArrayContentExpression(file);
                    requestInnerBlocks.AddLast(requestUsingStatement.WithStatement(innerBlock.AddStatements(
                        SyntaxFactory.ExpressionStatement(
                                RoslynExtensions.CreateMemberAssignmentExpression(
                                    RequestVariableName,
                                    RequestContentPropertyName,
                                    byteArrayContentExpression))
                            .AppendWhiteSpace())));
                }
            }

            var sendStatement = CreateSendStatement();
            if (!requestInnerBlocks.Any())
            {
                return new List<UsingStatementSyntax> { requestUsingStatement.WithStatement(innerBlock.AddStatements(sendStatement)) };
            }

            return requestInnerBlocks.Select(i => i.WithStatement(((BlockSyntax)i.Statement).AddStatements(sendStatement)));
        }

        /// <summary>
        /// Generate the statements for sending a HttpRequestMessage.
        /// </summary>
        /// <returns><see cref="LocalDeclarationStatementSyntax"/> statement.</returns>
        /// <remarks>
        /// var response = await httpClient.SendAsync(request);
        /// </remarks>
        private LocalDeclarationStatementSyntax CreateSendStatement()
        {
            var invocationExpressionSyntax = RoslynExtensions.CreateInvocationExpression(
                HttpClientVariableName,
                "SendAsync",
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(RequestVariableName)));

            var awaitExpression = SyntaxFactory.AwaitExpression(invocationExpressionSyntax);

            var declarationSyntax = RoslynExtensions.CreateVariableInitializationExpression("response", awaitExpression);

            return SyntaxFactory.LocalDeclarationStatement(declarationSyntax);
        }

        /// <summary>
        /// Generate the statements for HttpClient handler configuration.
        /// </summary>
        /// <param name="curlOptions">The curl options.</param>
        /// <param name="result">The result.</param>
        /// <returns>Collection of <see cref="MemberDeclarationSyntax" />.</returns>
        /// <remarks>
        /// var handler = new HttpClientHandler();
        /// handler.UseCookies = false;
        /// </remarks>
        private IEnumerable<MemberDeclarationSyntax> ConfigureHandlerStatements(
            CurlOptions curlOptions,
            ConvertResult<string> result)
        {
            var statementSyntaxs = new LinkedList<MemberDeclarationSyntax>();

            var handlerInitialization = RoslynExtensions.CreateVariableInitializationExpression(
                HandlerVariableName,
                RoslynExtensions.CreateObjectCreationExpression(nameof(HttpClientHandler)));
            statementSyntaxs.AddLast(
                SyntaxFactory.GlobalStatement(SyntaxFactory.LocalDeclarationStatement(handlerInitialization)));

            if (curlOptions.HasCookies)
            {
                var memberAssignmentExpression = RoslynExtensions.CreateMemberAssignmentExpression(
                    HandlerVariableName,
                    "UseCookies",
                    SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression));
                statementSyntaxs.AddLast(
                    SyntaxFactory.GlobalStatement(SyntaxFactory.ExpressionStatement(memberAssignmentExpression)));
            }

            if (curlOptions.HasProxy)
            {
                if (IsSupportedProxy(curlOptions.ProxyUri))
                {
                    var memberAssignmentExpression = RoslynExtensions.CreateMemberAssignmentExpression(
                        HandlerVariableName,
                        "Proxy",
                        RoslynExtensions.CreateObjectCreationExpression("WebProxy", RoslynExtensions.CreateStringLiteralArgument(curlOptions.ProxyUri.ToString())));

                    statementSyntaxs.AddLast(
                        SyntaxFactory.GlobalStatement(SyntaxFactory.ExpressionStatement(memberAssignmentExpression)));
                }
                else
                {
                    result.Warnings.Add($"Proxy scheme \"{curlOptions.ProxyUri.Scheme}\" is not supported");
                }
            }

            statementSyntaxs.TryAppendWhiteSpaceAtEnd();

            return statementSyntaxs;
        }
    }
}
