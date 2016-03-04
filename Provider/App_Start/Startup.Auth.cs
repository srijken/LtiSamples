﻿using System.Collections.Generic;
using System.Data.Entity;
using LtiLibrary.AspNet.Identity.Owin;
using LtiLibrary.Core.Common;
using LtiLibrary.Core.OAuth;
using LtiLibrary.Core.Outcomes.v1;
using LtiLibrary.Owin.Security.Lti;
using LtiLibrary.Owin.Security.Lti.Provider;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.Owin;
using Microsoft.Owin;
using Microsoft.Owin.Security.Cookies;
using Newtonsoft.Json;
using Owin;
using Provider.Lti;
using Provider.Models;
using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Provider
{
    public partial class Startup
    {
        // For more information on configuring authentication, please visit http://go.microsoft.com/fwlink/?LinkId=301864
        public void ConfigureAuth(IAppBuilder app)
        {
            // Configure the db context, user manager and signin manager to use a single instance per request
            app.CreatePerOwinContext(ProviderContext.Create);
            app.CreatePerOwinContext<ApplicationUserManager>(ApplicationUserManager.Create);
            app.CreatePerOwinContext<ApplicationSignInManager>(ApplicationSignInManager.Create);
            // The app also uses a RoleManager
            app.CreatePerOwinContext<ApplicationRoleManager>(ApplicationRoleManager.Create);

            // Enable the application to use a cookie to store information for the signed in user
            // and to use a cookie to temporarily store information about a user logging in with a third party login provider
            // Configure the sign in cookie
            app.UseCookieAuthentication(new CookieAuthenticationOptions
            {
                AuthenticationType = DefaultAuthenticationTypes.ApplicationCookie,
                CookieName = ".Provider.AspNet.Cookies",
                LoginPath = new PathString("/Account/Login"),
                Provider = new CookieAuthenticationProvider
                {
                    // Enables the application to validate the security stamp when the user logs in.
                    // This is a security feature which is used when you change a password or add an external login to your account.  
                    OnValidateIdentity = SecurityStampValidator.OnValidateIdentity<ApplicationUserManager, ApplicationUser>(
                        validateInterval: TimeSpan.FromMinutes(30),
                        regenerateIdentity: (manager, user) => user.GenerateUserIdentityAsync(manager))
                }
            });
            app.UseExternalSignInCookie(DefaultAuthenticationTypes.ExternalCookie);

            // Enables the application to temporarily store user information when they are verifying the second factor in the two-factor authentication process.
            app.UseTwoFactorSignInCookie(DefaultAuthenticationTypes.TwoFactorCookie, TimeSpan.FromMinutes(5));

            // Enables the application to remember the second login verification factor such as phone or email.
            // Once you check this option, your second step of verification during the login process will be remembered on the device where you logged in from.
            // This is similar to the RememberMe option when you log in.
            app.UseTwoFactorRememberBrowserCookie(DefaultAuthenticationTypes.TwoFactorRememberBrowserCookie);

            // Uncomment the following lines to enable logging in with third party login providers
            //app.UseMicrosoftAccountAuthentication(
            //    clientId: "",
            //    clientSecret: "");

            //app.UseTwitterAuthentication(
            //   consumerKey: "",
            //   consumerSecret: "");

            //app.UseFacebookAuthentication(
            //   appId: "",
            //   appSecret: "");

            //app.UseGoogleAuthentication(new GoogleOAuth2AuthenticationOptions()
            //{
            //    ClientId = "",
            //    ClientSecret = ""
            //});


            app.UseLtiAuthentication(new LtiAuthenticationOptions
            {
                Provider = new LtiAuthenticationProvider
                {
                    // Look up the secret for the consumer
                    OnAuthenticate = async context =>
                    {
                        // Make sure the request is not being replayed
                        var timeout = TimeSpan.FromMinutes(5);
                        var oauthTimestampAbsolute = OAuthConstants.Epoch.AddSeconds(context.LtiRequest.Timestamp);
                        if (DateTime.UtcNow - oauthTimestampAbsolute > timeout)
                        {
                            throw new LtiException("Expired " + OAuthConstants.TimestampParameter);
                        }

                        var db = context.OwinContext.Get<ProviderContext>();
                        var consumer = await db.Consumers.SingleOrDefaultAsync(c => c.Key == context.LtiRequest.ConsumerKey);
                        if (consumer == null)
                        {
                            throw new LtiException("Invalid " + OAuthConstants.ConsumerKeyParameter);
                        }

                        var signature = context.LtiRequest.GenerateSignature(consumer.Secret);
                        if (!signature.Equals(context.LtiRequest.Signature))
                        {
                            throw new LtiException("Invalid " + OAuthConstants.SignatureParameter);
                        }

                        // If we made it this far the request is valid
                    },

                    // Sign in using application authentication. This handler will create a new application
                    // user if no matching application user is found.
                    OnAuthenticated = async context =>
                    {
                        var db = context.OwinContext.Get<ProviderContext>();
                        var consumer = await db.Consumers.SingleOrDefaultAsync(c => c.Key.Equals(context.LtiRequest.ConsumerKey));
                        if (consumer == null) return;

                        // Record the request for logging purposes and as reference for outcomes
                        var providerRequest = new ProviderRequest
                        {
                            Received = DateTime.UtcNow,
                            LtiRequest = JsonConvert.SerializeObject(context.LtiRequest, Formatting.None, 
                            new JsonSerializerSettings {  NullValueHandling = NullValueHandling.Ignore })
                        };
                        db.ProviderRequests.Add(providerRequest);
                        db.SaveChanges();

                        // Add the requst ID as a claim
                        var claims = new List<Claim>
                        {
                            new Claim("ProviderRequestId",
                                providerRequest.ProviderRequestId.ToString(CultureInfo.InvariantCulture))
                        };

                        // Outcomes can live a long time to give the teacher enough
                        // time to grade the assignment. So they are stored in a separate table.
                        var lisOutcomeServiceUrl = ((IOutcomesManagementRequest) context.LtiRequest).LisOutcomeServiceUrl;
                        var lisResultSourcedid = ((IOutcomesManagementRequest)context.LtiRequest).LisResultSourcedId;
                        if (!string.IsNullOrWhiteSpace(lisOutcomeServiceUrl)
                            && !string.IsNullOrWhiteSpace(lisResultSourcedid))
                        {
                            var outcome = await db.Outcomes.SingleOrDefaultAsync(o =>
                                o.ConsumerId == consumer.ConsumerId
                                && o.LisResultSourcedId == lisResultSourcedid);

                            if (outcome == null)
                            {
                                outcome = new Outcome
                                {
                                    ConsumerId = consumer.ConsumerId,
                                    LisResultSourcedId = lisResultSourcedid
                                };
                                db.Outcomes.Add(outcome);
                                await db.SaveChangesAsync(); // Assign OutcomeId;
                            }
                            outcome.ContextTitle = context.LtiRequest.ContextTitle;
                            outcome.ServiceUrl = lisOutcomeServiceUrl;
                            await db.SaveChangesAsync();

                            // Add the outcome ID as a claim
                            claims.Add(new Claim("OutcomeId",
                                outcome.OutcomeId.ToString(CultureInfo.InvariantCulture)));
                        }

                        // Sign in
                        await SecurityHandler.OnAuthenticated<ApplicationUserManager, ApplicationUser>(
                            context, claims);
                    },

                    // Generate a username using the LisPersonEmailPrimary from the LTI request
                    OnGenerateUserName = context =>
                                         {
                                             context.UserName = context.GenerateUserName();
                                             return Task.FromResult<object>(null);
                                         }
                },
                ChallengeResultUrl = new PathString("/Manage/ToolConsumerLogins"),
                SignInAsAuthenticationType = DefaultAuthenticationTypes.ApplicationCookie
            });

        }
    }
}