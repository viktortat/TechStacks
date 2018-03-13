﻿using System;
using System.Linq;
using System.Threading.Tasks;
using ServiceStack;
using ServiceStack.OrmLite;
using TechStacks.ServiceModel;
using TechStacks.ServiceModel.Types;

namespace TechStacks.ServiceInterface
{
    [Authenticate]
    public class PostUserServices : PostServicesBase
    {
        public async Task<GetUserPostActivityResponse> Get(GetUserPostActivity request)
        {
            var userId = GetUserId();
            var recentVotes = DateTime.Now.AddMonths(-6);

            var q = Db.From<PostVote>()
                .Where(x => x.UserId == userId && x.Created > recentVotes)
                .Select(x => new { x.PostId, x.Weight });

            var postVotes = await Db.SelectAsync<(long postId, int weight)>(q);

            var favoritePostIds = await Db.ColumnAsync<long>(Db.From<PostFavorite>()
                .Where(x => x.UserId == userId)
                .Select(x => x.PostId));

            return new GetUserPostActivityResponse
            {
                UpVotedPostIds = postVotes.Where(x => x.weight > 0).Map(x => x.postId),
                DownVotedPostIds = postVotes.Where(x => x.weight < 0).Map(x => x.postId),
                FavoritePostIds = favoritePostIds,
            };
        }

        public async Task<GetUserOrganizationsResponse> Get(GetUserOrganizations request)
        {
            var userId = GetUserId();
            return new GetUserOrganizationsResponse
            {
                Members = await Db.SelectAsync<OrganizationMember>(x => x.UserId == userId),
                MemberInvites = await Db.SelectAsync<OrganizationMemberInvite>(x => x.UserId == userId && x.Approved == null && x.Dismissed == null),
            };
        }

        public async Task<UserPostVoteResponse> Put(UserPostVote request)
        {
            if (request.Id <= 0)
                throw new ArgumentNullException(nameof(request.Id));

            var user = GetUser();
            var post = await AssertPostAsync(request.Id);
            var groupMember = AssertCanAnnotateOnOrganization(Db, post.OrganizationId, user);
            AssertCanAnnotatePost(post, user, groupMember);

            var userId = GetUserId();

            await Db.DeleteAsync<PostVote>(x => x.UserId == userId && x.PostId == request.Id);
            if (request.Weight != 0)
            {
                await Db.InsertAsync(new PostVote
                {
                    UserId = userId,
                    PostId = request.Id,
                    Weight = request.Weight < 0 ? -1 : 1,
                    Created = DateTime.Now,
                });
            }

            await Db.ExecuteSqlAsync(
                @"update post set 
                         up_votes   = (select count(*) from post_vote where post_id = @id and weight > 0), 
                         down_votes = (select count(*) from post_vote where post_id = @id and weight < 0)
                   where id = @id", new { id = request.Id });

            await Db.ExecuteSqlAsync(
                @"update user_activity set 
                         post_up_votes   = (select count(*) from post_vote v 
                                              join post p on p.id = v.post_id 
                                             where p.user_id = user_activity.id and weight > 0),
                         post_down_votes = (select count(*) from post_vote v 
                                              join post p on p.id = v.post_id 
                                             where p.user_id = user_activity.id and weight < 0)
                   where id = (select user_id from post where id = @id)",
                new { id = request.Id });

            ClearPostCaches();

            return new UserPostVoteResponse();
        }

        public async Task<UserPostFavoriteResponse> Put(UserPostFavorite request)
        {
            if (request.Id <= 0)
                throw new ArgumentNullException(nameof(request.Id));

            var user = GetUser();
            var post = await AssertPostAsync(request.Id);
            var groupMember = AssertCanAnnotateOnOrganization(Db, post.OrganizationId, user);
            AssertCanAnnotatePost(post, user, groupMember);

            var userId = GetUserId();

            var exists = await Db.DeleteAsync<PostFavorite>(x => x.UserId == userId && x.PostId == request.Id);

            if (exists == 0)
            {
                await Db.InsertAsync(new PostFavorite
                {
                    UserId = userId,
                    PostId = request.Id,
                    Created = DateTime.Now,
                });
            }

            await Db.ExecuteSqlAsync(
                @"update post set 
                         favorites = (select count(*) from post_favorite where post_id = @id)
                   where id = @id", new { id = request.Id });

            return new UserPostFavoriteResponse();
        }

        public object Put(UserPostReport request)
        {
            if (request.Id <= 0)
                throw new ArgumentNullException(nameof(request.Id));

            var user = GetUser();
            var post = AssertPost(request.Id);
            var groupMember = AssertCanAnnotateOnOrganization(Db, post.OrganizationId, user, out var org);
            AssertCanAnnotatePost(post, user, groupMember);

            var userId = GetUserId();

            Db.Delete<PostReport>(x => x.UserId == userId && x.PostId == request.Id);
            Db.Insert(new PostReport
            {
                UserId = userId,
                UserName = user.UserName,
                OrganizationId = org.Id,
                PostId = request.Id,
                FlagType = request.FlagType,
                ReportNotes = request.ReportNotes,
                Created = DateTime.Now,
            });

            var reportsCount = Db.Count<PostReport>(x => x.OrganizationId == org.Id && x.PostId == request.Id);
            if (reportsCount >= org.DeletePostsWithReportCount)
            {
                Db.UpdateOnly(() => new Post { Deleted = DateTime.Now, DeletedBy = nameof(PostReport) },
                    @where: x => x.Id == request.Id);
            }

            Db.ExecuteSql(
                @"update post set 
                         report_count = (select count(*) from post_report where organization_id = @orgId and post_id = @id)
                   where id = @id", new { orgId = org.Id, id = request.Id });

            return new UserPostReportResponse();
        }

        public async Task<object> Put(UserPostCommentVote request)
        {
            if (request.Id <= 0)
                throw new ArgumentNullException(nameof(request.Id));
            if (request.PostId <= 0)
                throw new ArgumentNullException(nameof(request.PostId));

            var user = GetUser();
            var post = await AssertPostAsync(request.PostId);
            var groupMember = AssertCanAnnotateOnOrganization(Db, post.OrganizationId, user);
            AssertCanAnnotatePost(post, user, groupMember);

            var userId = GetUserId();

            await Db.DeleteAsync<PostCommentVote>(x => x.UserId == userId && x.PostCommentId == request.Id);
            if (request.Weight != 0)
            {
                await Db.InsertAsync(new PostCommentVote
                {
                    UserId = userId,
                    PostId = request.PostId,
                    PostCommentId = request.Id,
                    Weight = request.Weight < 0 ? -1 : 1,
                    Created = DateTime.Now,
                });
            }

            await Db.ExecuteSqlAsync(
                @"update post_comment set 
                         up_votes   = (select count(*) from post_comment_vote where post_comment_id = @id and weight > 0), 
                         down_votes = (select count(*) from post_comment_vote where post_comment_id = @id and weight < 0)
                   where id = @id",
                new { id = request.Id });

            await Db.ExecuteSqlAsync(
                @"update user_activity set 
                         comment_up_votes   = (select count(*) from post_comment_vote v 
                                                 join post_comment c on c.id = v.post_comment_id 
                                                where c.user_id = user_activity.id and weight > 0),
                         comment_down_votes = (select count(*) from post_comment_vote v 
                                                 join post_comment c on c.id = v.post_comment_id 
                                                where c.user_id = user_activity.id and weight < 0)
                   where id = (select user_id from post_comment where id = @id)",
                new { id = request.Id });

            ClearPostCaches();

            return new UserPostCommentVoteResponse();
        }

        public object Put(UserPostCommentReport request)
        {
            if (request.Id <= 0)
                throw new ArgumentNullException(nameof(request.Id));
            if (request.PostId <= 0)
                throw new ArgumentNullException(nameof(request.PostId));

            var user = GetUser();
            var post = AssertPost(request.PostId);
            var groupMember = AssertCanAnnotateOnOrganization(Db, post.OrganizationId, user, out var org);
            AssertCanAnnotatePost(post, user, groupMember);

            var userId = user.GetUserId();
            Db.Delete<PostCommentReport>(x => x.UserId == userId && x.PostCommentId == request.Id);
            Db.Insert(new PostCommentReport
            {
                UserId = userId,
                UserName = user.UserName,
                OrganizationId = org.Id,
                PostId = post.Id,
                PostCommentId = request.Id,
                FlagType = request.FlagType,
                ReportNotes = request.ReportNotes,
                Created = DateTime.Now,
            });

            var reportsCount = Db.Count<PostCommentReport>(x => x.OrganizationId == org.Id && x.PostCommentId == request.Id);
            if (reportsCount >= org.DeletePostsWithReportCount)
            {
                Db.UpdateOnly(() => new PostComment { Deleted = DateTime.Now, DeletedBy = nameof(PostCommentReport) },
                    where: x => x.Id == request.Id);
            }

            Db.ExecuteSql(
                @"update post_comment set 
                         report_count = (select count(*) from post_comment_report where organization_id = @orgId and post_comment_id = @Id)
                   where id = @id", new { orgId = org.Id, request.Id });

            return new UserPostCommentReportResponse();
        }

    }
}